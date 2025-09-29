// Assets/Scripts/Camera/IsometricCamera.cs
// Defaults set to match the screenshot: Yaw=45, Pitch=35, Distance=(100.2, -3.6), Lerp=12, Ortho=on, OrthoSize=23, FOV=50.
using UnityEngine;

[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class IsometricCamera : MonoBehaviour
{
    [Header("Isometric Framing")]
    [Range(-180f, 180f)] public float yaw = 45f;
    [Range(1f, 89f)] public float pitch = 35f;
    [Tooltip("X = planar distance, Y = vertical offset")]
    public Vector2 distance = new Vector2(100.2f, -3.6f);
    [Min(0f)] public float followLerp = 12f;

    [Header("Projection")]
    public bool useOrthographic = true;
    [Min(0.01f)] public float orthographicSize = 23f;
    [Range(1f, 179f)] public float perspectiveFov = 50f;

    [Header("Follow Target (optional)")]
    public Transform follow;

    Camera _cam;

    void OnEnable() { _cam = GetComponent<Camera>(); if (!_cam) _cam = gameObject.AddComponent<Camera>(); }
    void OnValidate() { ApplyProjection(); }
    void Update() { ApplyProjection(); }
    void LateUpdate()
    {
        if (!follow) return;

        var yawQ = Quaternion.Euler(0f, yaw, 0f);
        var pitchQ = Quaternion.Euler(pitch, 0f, 0f);
        var rot = yawQ * pitchQ;

        var offsetPlanar = rot * (Vector3.back * Mathf.Max(0f, distance.x));
        var offset = offsetPlanar + Vector3.up * distance.y;

        var desiredPos = follow.position + offset;
        var desiredRot = Quaternion.LookRotation((follow.position - desiredPos).normalized, Vector3.up);

        if (followLerp <= 0f)
        {
            transform.SetPositionAndRotation(desiredPos, desiredRot);
        }
        else
        {
            float t = 1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, desiredPos, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, t);
        }
    }

    void ApplyProjection()
    {
        if (!_cam) return;
        _cam.orthographic = useOrthographic;
        if (_cam.orthographic) _cam.orthographicSize = orthographicSize;
        else _cam.fieldOfView = perspectiveFov;
    }
}
