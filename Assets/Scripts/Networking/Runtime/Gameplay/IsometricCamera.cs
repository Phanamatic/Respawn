using UnityEngine;

namespace Game.Net
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class IsometricCamera : MonoBehaviour
    {
        [Header("Isometric Framing")]
        [Tooltip("World yaw around +Y")]
        [SerializeField] private float yaw = 45f;
        [Tooltip("Tilt from top-down")]
        [SerializeField] private float pitch = 35f;
        [Tooltip("Offset in camera local Z (back) and Y (up) after yaw/pitch")]
        [SerializeField] private Vector2 distance = new Vector2(12f, 12f); // (back, up)
        [SerializeField, Min(0f)] private float followLerp = 12f;

        [Header("Projection")]
        [SerializeField] private bool useOrthographic = false;
        [SerializeField, Min(0.1f)] private float orthographicSize = 9f;
        [SerializeField, Range(20f, 90f)] private float perspectiveFov = 50f;

        private Transform _target;
        private Camera _cam;
        private Quaternion _isoRot;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            ApplyProjection();
            _isoRot = Quaternion.Euler(pitch, yaw, 0f);
            // lock camera rotation
            transform.rotation = _isoRot;
        }

        private void OnValidate()
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            ApplyProjection();
        }

        private void ApplyProjection()
        {
            if (_cam == null) return;
            _cam.orthographic = useOrthographic;
            if (useOrthographic) _cam.orthographicSize = orthographicSize;
            else _cam.fieldOfView = perspectiveFov;
        }

        public void SetTarget(Transform t) => _target = t;

        private void LateUpdate()
        {
            if (_target == null) return;

            // Desired camera position in world given isometric rotation and distances
            // Local back (Z-) by distance.x, local up (Y+) by distance.y
            var localOffset = new Vector3(0f, distance.y, -distance.x);
            var desiredPos = _target.position + _isoRot * localOffset;

            // Smooth follow
            transform.position = Vector3.Lerp(transform.position, desiredPos, 1f - Mathf.Exp(-followLerp * Time.deltaTime));

            // Keep rotation fixed
            transform.rotation = _isoRot;
        }
    }
}
