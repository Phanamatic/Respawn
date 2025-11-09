using UnityEngine;

namespace UI.Scripts.Universal
{
    /// <summary>
    /// Simple script to continuously rotate an object on specified axes.
    /// Attach to any GameObject you want to rotate.
    /// </summary>
    public class RotateObject : MonoBehaviour
    {
        [Header("Rotation Settings")]
        [Tooltip("Enable rotation on X axis")]
        [SerializeField] private bool rotateX = false;

        [Tooltip("Enable rotation on Y axis")]
        [SerializeField] private bool rotateY = true;

        [Tooltip("Enable rotation on Z axis")]
        [SerializeField] private bool rotateZ = false;

        [Header("Speed Settings")]
        [Tooltip("Rotation speed in degrees per second")]
        [SerializeField] private float rotationSpeed = 50f;

        [Header("Advanced")]
        [Tooltip("Use unscaled time (ignores Time.timeScale)")]
        [SerializeField] private bool useUnscaledTime = false;

        [Tooltip("Rotation space (Self = local, World = world space)")]
        [SerializeField] private Space rotationSpace = Space.World;

        private void Update()
        {
            // Calculate rotation amount based on time
            float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float rotationAmount = rotationSpeed * deltaTime;

            // Build rotation vector based on enabled axes
            Vector3 rotation = Vector3.zero;

            if (rotateX) rotation.x = rotationAmount;
            if (rotateY) rotation.y = rotationAmount;
            if (rotateZ) rotation.z = rotationAmount;

            // Apply rotation
            transform.Rotate(rotation, rotationSpace);
        }
    }
}
