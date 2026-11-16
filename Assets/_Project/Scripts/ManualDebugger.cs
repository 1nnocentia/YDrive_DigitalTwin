using UnityEngine;
using UnityEngine.InputSystem;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

public class ManualDebugger : MonoBehaviour
{
    [Header("Network ESP32")]
    public string esp32IP = "192.168.4.1";
    public int tcpPort = 4210;

    [Header("Kecepatan Manual")]
    public float moveSpeed = 0.5f;
    public float rotSpeed = 0.5f;

    private TcpClient tcp;
    private NetworkStream stream;
    private bool connecting;

    private float sendTimer = 0f;
    private const float SEND_INTERVAL = 0.1f;

    void Start()
    {
        ConnectAsync();
    }

    void Update()
    {
        float vx = 0f;
        float vy = 0f;
        float w = 0f;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) vy = moveSpeed;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) vy = -moveSpeed;
            
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) vx = moveSpeed;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) vx = -moveSpeed;
            
            if (Keyboard.current.eKey.isPressed) w = rotSpeed;
            if (Keyboard.current.qKey.isPressed) w = -rotSpeed;
        }

        sendTimer += Time.deltaTime;
        if (sendTimer >= SEND_INTERVAL)
        {
            sendTimer -= SEND_INTERVAL;
            SendCmd(vx, vy, w);
        }
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
                Debug.LogError("TCP Connection Timeout");
                return; 
            }
            await t;
            tcp = c;
            stream = c.GetStream();
            stream.WriteTimeout = 200;
            Debug.Log("TCP Connected to ESP32");
        }
        catch (System.Exception e) { Debug.LogWarning("Exception: " + e.Message); }
        finally { connecting = false; }
    }

    private void SendCmd(float vx, float vy, float w)
    {
        if (stream == null) { ConnectAsync(); return; }
        try
        {
            byte[] data = Encoding.ASCII.GetBytes($"{vx:F2},{vy:F2},{w:F2}\n");
            stream.Write(data, 0, data.Length);
            
            if (vx != 0 || vy != 0 || w != 0)
            {
                Debug.Log($"Sent: {vx:F2}, {vy:F2}, {w:F2}");
            }
        }
        catch
        {
            stream = null; tcp?.Close(); tcp = null;
        }
    }

    void OnDestroy()
    {
        stream?.Close();
        tcp?.Close();
    }
}