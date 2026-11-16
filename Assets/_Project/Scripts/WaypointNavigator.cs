using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

[DefaultExecutionOrder(100)]
public class WaypointNavigator : MonoBehaviour
{
    public enum RobotState { Idle, Moving, Terminated }

    [Header("References")]
    public DigitalTwinManager manager;
    public Transform waypointMarker;

    [Header("Tick")]
    public float tickHz = 5f;
    public int pulseMs = 120;

    [Header("Speed (%)")]
    public int minSpeedPct = 30;
    public int maxSpeedPct = 60;
    public float slowRadius = 0.4f;

    [Header("Tolerance")]
    public float stopRadius = 0.05f;
    public int arriveTicks = 2;
    public float axisTol = 0.03f;
    public bool holdHeading = true;
    public float yawTol = 12f;

    [Header("Security")]
    public float maxWaypointRadius = 1.5f;
    public float maxMoveSeconds = 15f;
    public float noProgressSeconds = 3f;
    public float minProgress = 0.02f;
    public float lostAbortSeconds = 5f;

    [Header("Network ESP32")]
    public string esp32IP = "192.168.4.1";
    public int tcpPort = 4210;

    [Header("Debug")]
    public bool enableLog = true;
    public bool showTestPanel = true;
    public int testPulseMs = 400;
    public int testSpeedPct = 40;

    public RobotState state = RobotState.Idle;

    private TcpClient tcp;
    private NetworkStream stream;
    private bool connecting;

    private Vector2 targetF;
    private float lockYaw;
    private float tickTimer, idleTimer, lostTimer, moveTimer, progressTimer, bestDist, testUntil;
    private float fps = 30f, lastWriteMs;
    private int arriveCount;
    private string lastCmd = "S", statusMsg = "Siap";
    private float dbgF, dbgR, dbgDist, dbgYawErr;
    private StreamWriter logWriter;

    private Rect rMain, rToggle;
    private readonly Rect[] rTests = new Rect[6];
    private static readonly string[] testNames = { "Backward", "Forward", "Left", "Right", "CW", "CCW" };
    private static readonly string[] testCodes = { "F", "B", "L", "R", "C", "A" };

    public float markerLift = 0.05f;

    void Start()
    {
        ConnectAsync();
        HideMarker();

        if (enableLog)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, $"log_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
                logWriter = new StreamWriter(path) { AutoFlush = true };
                logWriter.WriteLine("t,state,robotTrk,floorTrk,posX,posZ,yaw,tgtX,tgtZ,fwdErr,rightErr,dist,yawErr,cmd,fps,writeMs,conn");
                Debug.Log("Log: " + path);
            }
            catch (System.Exception e) { Debug.LogWarning("Log gagal dibuat: " + e.Message); }
        }
    }

    void LateUpdate()
    {
        fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f), 0.05f);

        HandleInput();
        UpdateMarker();

        float period = 1f / Mathf.Max(1f, tickHz);
        tickTimer += Time.deltaTime;
        if (tickTimer >= period)
        {
            tickTimer = Mathf.Min(tickTimer - period, period);
            Tick(period);
        }
    }

    void HandleInput()
    {
        Vector2 sp;
        if (Touchscreen.current != null)
        {
            var touch = Touchscreen.current.primaryTouch;
            if (!touch.press.wasPressedThisFrame) return;
            sp = touch.position.ReadValue();
        }
        else if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
        {
            sp = Pointer.current.position.ReadValue();
        }
        else return;

        Layout();
        if (OverImGui(sp)) { statusMsg = "Tap terblokir UI kanan"; return; }
        if (EventSystem.current != null)
        {
            int id = Touchscreen.current != null ? Touchscreen.current.primaryTouch.touchId.ReadValue() : -1;
            if (EventSystem.current.IsPointerOverGameObject(id)) { statusMsg = "Tap terblokir Canvas"; return; }
        }

        if (state == RobotState.Terminated) { statusMsg = "Terminated: RESET first"; return; }
        if (manager == null || !manager.BothTracked || !manager.HasPose) { statusMsg = "Target robot/floor is missing"; return; }

        Camera cam = Camera.main;
        if (cam == null) return;

        Transform fl = manager.floorTarget;
        Ray ray = cam.ScreenPointToRay(sp);
        Plane ground = new Plane(fl.up, fl.position);
        if (!ground.Raycast(ray, out float d))
        {
            statusMsg = "Sudut tablet terlalu mendatar (lewat cakrawala)";
            return;
        }

        Vector3 f3 = manager.WorldToFloor(ray.GetPoint(d));
        Vector2 t = new Vector2(f3.x, f3.z);
        Vector2 off = t - manager.PosF;
        float want = off.magnitude;
        if (want > maxWaypointRadius) t = manager.PosF + off.normalized * maxWaypointRadius;

        targetF = t;
        lockYaw = manager.YawF;
        arriveCount = 0; moveTimer = 0f; progressTimer = 0f; lostTimer = 0f;
        bestDist = float.MaxValue;
        state = RobotState.Moving;
        statusMsg = $"Tap {want:F2} m from robot" + (want > maxWaypointRadius ? $" (Cut to {maxWaypointRadius:F1})" : "");
        if (waypointMarker != null) waypointMarker.gameObject.SetActive(true);
        Handheld.Vibrate();
    }
    
    void UpdateMarker()
    {
        if (waypointMarker != null && state == RobotState.Moving &&
            manager != null && manager.floorTarget != null)
        {
            Transform fl = manager.floorTarget;
            waypointMarker.SetPositionAndRotation(
                manager.FloorToWorld(targetF) + fl.up * markerLift,
                fl.rotation);
        }
    }

    void HideMarker()
    {
        if (waypointMarker != null) waypointMarker.gameObject.SetActive(false);
    }

    void Tick(float dt)
    {
        lastCmd = "S";

        if (Time.time < testUntil) { LogRow(); return; }

        if (state != RobotState.Moving)
        {
            idleTimer += dt;
            if (idleTimer >= 1f) { idleTimer = 0f; SendLine("S"); }
            LogRow();
            return;
        }

        if (manager == null || !manager.BothTracked || !manager.HasPose)
        {
            lostTimer += dt;
            SendLine("S");
            statusMsg = "Tracking lost, robot terminate";
            if (lostTimer > lostAbortSeconds) Terminate("Tracking lost > " + lostAbortSeconds + " seconds");
            LogRow();
            return;
        }
        lostTimer = 0f;
        moveTimer += dt;

        Vector2 pos = manager.PosF;
        float yaw = manager.YawF;
        Vector2 e = targetF - pos;
        float dist = e.magnitude;

        float rad = yaw * Mathf.Deg2Rad;
        Vector2 fwd = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        Vector2 right = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
        float f = Vector2.Dot(e, fwd);
        float r = Vector2.Dot(e, right);
        float yawErr = holdHeading ? Mathf.DeltaAngle(yaw, lockYaw) : 0f;
        dbgF = f; dbgR = r; dbgDist = dist; dbgYawErr = yawErr;

        if (dist <= stopRadius)
        {
            SendLine("S");
            arriveCount++;
            if (arriveCount >= arriveTicks)
            {
                state = RobotState.Idle;
                statusMsg = "Arrived at Waypoint";
                HideMarker();
            }
            LogRow();
            return;
        }
        arriveCount = 0;

        if (dist < bestDist - minProgress) { bestDist = dist; progressTimer = 0f; }
        else progressTimer += dt;
        if (moveTimer > maxMoveSeconds) { Terminate("More than " + maxMoveSeconds + " seconds"); LogRow(); return; }
        if (progressTimer > noProgressSeconds) { Terminate("No progress in " + noProgressSeconds + " seconds"); LogRow(); return; }

        string letters = "";
        if (f > axisTol) letters += "F"; else if (f < -axisTol) letters += "B";
        if (r > axisTol) letters += "R"; else if (r < -axisTol) letters += "L";
        if (holdHeading && Mathf.Abs(yawErr) > yawTol) letters += yawErr > 0f ? "C" : "A";

        if (letters.Length == 0) { SendLine("S"); LogRow(); return; }

        int speed = Mathf.RoundToInt(Mathf.Lerp(minSpeedPct, maxSpeedPct, Mathf.InverseLerp(stopRadius, slowRadius, dist)));
        int ms = Mathf.Clamp(pulseMs, 20, 500);
        lastCmd = letters + speed;
        SendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1},{2}", letters, speed, ms));
        LogRow();
    }

    public void Terminate(string reason = "Manual")
    {
        state = RobotState.Terminated;
        statusMsg = "TERMINATED: " + reason;
        HideMarker();
        for (int i = 0; i < 3; i++) SendLine("S");
        Debug.LogWarning("TERMINATED: " + reason);
    }

    public void ResetTerminate()
    {
        if (state != RobotState.Terminated) return;
        state = RobotState.Idle;
        statusMsg = "Ready";
    }

    public void EmergencyStop() { Terminate("EmergencyStop"); }

    void OnApplicationPause(bool paused)
    {
        if (paused && state == RobotState.Moving) Terminate("Paused");
    }

    async void ConnectAsync()
    {
        if (connecting) return;
        connecting = true;
        try
        {
            var c = new TcpClient { NoDelay = true };
            var t = c.ConnectAsync(esp32IP, tcpPort);
            if (await Task.WhenAny(t, Task.Delay(2000)) != t)
            {
                c.Close();
                Debug.LogWarning($"TCP timeout {esp32IP}:{tcpPort}");
                return;
            }
            await t;
            tcp = c;
            stream = c.GetStream();
            stream.WriteTimeout = 200;
            Debug.Log("TCP connected");
        }
        catch (System.Exception e) { Debug.LogWarning("TCP: " + e.Message); }
        finally { connecting = false; }
    }

    void SendLine(string line)
    {
        if (stream == null) { ConnectAsync(); return; }
        try
        {
            byte[] d = Encoding.ASCII.GetBytes(line + "\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            stream.Write(d, 0, d.Length);
            lastWriteMs = (float)sw.Elapsed.TotalMilliseconds;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("Write failed: " + ex.Message);
            try { stream?.Close(); tcp?.Close(); } catch { }
            stream = null; tcp = null;
        }
    }

    void LogRow()
    {
        if (logWriter == null) return;
        Vector2 p = manager != null ? manager.PosF : Vector2.zero;
        float yaw = manager != null ? manager.YawF : 0f;
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1},{2},{3},{4:F3},{5:F3},{6:F1},{7:F3},{8:F3},{9:F3},{10:F3},{11:F3},{12:F1},{13},{14:F0},{15:F1},{16}",
            Time.realtimeSinceStartup, state,
            manager != null && manager.RobotTracked ? 1 : 0,
            manager != null && manager.FloorTracked ? 1 : 0,
            p.x, p.y, yaw, targetF.x, targetF.y, dbgF, dbgR, dbgDist, dbgYawErr,
            lastCmd, fps, lastWriteMs, stream != null ? 1 : 0));
    }

    void Layout()
    {
        float m = Screen.height * 0.015f;
        float w = Mathf.Min(Screen.width * 0.30f, Screen.height * 0.40f);
        float h = Screen.height * 0.09f;
        float x = Screen.width - w - m;
        rMain = new Rect(x, m, w, h * 1.4f);
        rToggle = new Rect(x, rMain.yMax + m, w, h * 0.7f);
        float y = rToggle.yMax + m;
        for (int i = 0; i < rTests.Length; i++)
        {
            rTests[i] = new Rect(x, y, w, h * 0.6f);
            y += h * 0.6f + m * 0.5f;
        }
    }

    bool OverImGui(Vector2 sp)
    {
        Vector2 g = new Vector2(sp.x, Screen.height - sp.y);
        if (rMain.Contains(g) || rToggle.Contains(g)) return true;
        if (showTestPanel)
            foreach (var r in rTests) if (r.Contains(g)) return true;
        return false;
    }

    void PulseTest(string code)
    {
        if (state != RobotState.Idle) { statusMsg = "Tes hanya saat Idle"; return; }
        SendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1},{2}", code, testSpeedPct, testPulseMs));
        testUntil = Time.time + testPulseMs / 1000f + 0.15f;
        lastCmd = code;
        statusMsg = "TEST " + code;
    }

    void OnGUI()
    {
        Layout();
        var big = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(rMain.height * 0.32f), fontStyle = FontStyle.Bold };
        var small = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(rTests[0].height * 0.38f) };
        var lbl = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.022f) };
        lbl.normal.textColor = Color.white;

        string trk = manager != null ? $"Robot:{(manager.RobotTracked ? "OK" : "--")} Floor:{(manager.FloorTracked ? "OK" : "--")}" : "manager?";
        GUI.Label(new Rect(16, 16, Screen.width * 0.62f, Screen.height * 0.25f),
            $"{state}  dist={dbgDist:F2} m  cmd={lastCmd}\n{trk}  tcp:{(stream != null ? "OK" : "--")}  {fps:F0}fps\n{statusMsg}", lbl);

        Color old = GUI.backgroundColor;
        if (state == RobotState.Terminated)
        {
            GUI.backgroundColor = Color.green;
            if (GUI.Button(rMain, "RESET", big)) ResetTerminate();
        }
        else
        {
            GUI.backgroundColor = Color.red;
            if (GUI.Button(rMain, "TERMINATE", big)) Terminate("Button");
        }
        GUI.backgroundColor = old;

        if (GUI.Button(rToggle, "TEST", small)) showTestPanel = !showTestPanel;
        if (showTestPanel)
        {
            for (int i = 0; i < rTests.Length; i++)
                if (GUI.Button(rTests[i], testNames[i], small)) PulseTest(testCodes[i]);
        }
    }

    void OnDestroy()
    {
        try { SendLine("S"); } catch { }
        stream?.Close();
        tcp?.Close();
        logWriter?.Close();
    }
}