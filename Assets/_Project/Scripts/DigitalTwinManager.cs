using UnityEngine;

[DefaultExecutionOrder(-100)]
public class DigitalTwinManager : MonoBehaviour
{
    [Header("Targets")]
    public Transform robotTarget;
    public Transform floorTarget;
    public Transform digitalTwin;
    public GameObject popupCanvas;

    [Header("Calibration")]
    public float yawOffset = 0f; 
    public Vector3 localOffset = Vector3.zero;

    [Header("Filter")]
    public float positionSmoothing = 15f;
    public float yawSmoothing = 10f;
    public float maxJumpMeters = 0.15f;
    public float maxYawJumpDeg = 35f;
    public int maxRejectFrames = 10;

    public bool RobotTracked { get; private set; }
    public bool FloorTracked { get; private set; }
    public bool BothTracked => RobotTracked && FloorTracked;
    public bool HasPose { get; private set; }
    public Vector2 PosF { get; private set; }
    public float YawF { get; private set; }

    private bool needSnap = true;
    private int rejects;

    public void OnRobotFound() { RobotTracked = true; needSnap = true; }
    public void OnRobotLost()  { RobotTracked = false; }
    public void OnFloorFound() { FloorTracked = true; needSnap = true; }
    public void OnFloorLost()  { FloorTracked = false; }

    public Vector3 WorldToFloor(Vector3 world)
    {
        return Quaternion.Inverse(floorTarget.rotation) * (world - floorTarget.position);
    }

    public Vector3 FloorToWorld(Vector2 f)
    {
        return floorTarget.position + floorTarget.rotation * new Vector3(f.x, 0f, f.y);
    }

    void Start()
    {
        if (popupCanvas != null) popupCanvas.SetActive(true);
        if (digitalTwin != null) digitalTwin.gameObject.SetActive(true);
        // if (digitalTwin != null) digitalTwin.gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        bool both = BothTracked && robotTarget != null && floorTarget != null;

        if (popupCanvas != null && popupCanvas.activeSelf == both) popupCanvas.SetActive(!both);
        // if (digitalTwin != null && digitalTwin.gameObject.activeSelf != both) digitalTwin.gameObject.SetActive(both);

        if (!both)
        {
            HasPose = false;
            needSnap = true;
            return;
        }

        Quaternion invF = Quaternion.Inverse(floorTarget.rotation);
        Vector3 p = invF * (robotTarget.position + robotTarget.rotation * localOffset - floorTarget.position);
        Vector3 f = invF * (robotTarget.rotation * Vector3.forward);
        if (f.x * f.x + f.z * f.z < 1e-4f) return;

        Vector2 pos = new Vector2(p.x, p.z);
        float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + yawOffset;

        if (needSnap)
        {
            PosF = pos; YawF = yaw; needSnap = false; rejects = 0;
        }
        else
        {
            bool jump = Vector2.Distance(PosF, pos) > maxJumpMeters ||
                        Mathf.Abs(Mathf.DeltaAngle(YawF, yaw)) > maxYawJumpDeg;
            if (jump && rejects < maxRejectFrames)
            {
                rejects++; 
            }
            else if (jump)
            {
                PosF = pos; YawF = yaw; rejects = 0; 
            }
            else
            {
                rejects = 0;
                float kp = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
                float ky = 1f - Mathf.Exp(-yawSmoothing * Time.deltaTime);
                PosF = Vector2.Lerp(PosF, pos, kp);
                YawF = Mathf.LerpAngle(YawF, yaw, ky);
            }
        }
        HasPose = true;

        if (digitalTwin != null)
        {
            digitalTwin.SetPositionAndRotation(
                FloorToWorld(PosF),
                floorTarget.rotation * Quaternion.Euler(0f, YawF, 0f));
        }
    }
}