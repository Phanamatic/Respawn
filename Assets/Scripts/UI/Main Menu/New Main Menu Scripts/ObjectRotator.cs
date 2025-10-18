using UnityEngine;

namespace Game.UI.MainMenu
{
    /// <summary>
    /// Rotates any GameObject continuously around a specified axis
    /// Useful for rotating 3D models, props, or any object in the scene
    /// </summary>
    public class ObjectRotator : MonoBehaviour
    {
        [Header("Rotation Settings")]
        [Tooltip("Speed of rotation in degrees per second")]
        [SerializeField] private float rotationSpeed = 50f;

        [Header("Rotation Axis")]
        [Tooltip("Rotate around X axis")]
        [SerializeField] private bool rotateX = false;

        [Tooltip("Rotate around Y axis")]
        [SerializeField] private bool rotateY = true;

        [Tooltip("Rotate around Z axis")]
        [SerializeField] private bool rotateZ = false;

        [Header("Advanced Options")]
        [Tooltip("Use local space instead of world space")]
        [SerializeField] private bool useLocalSpace = true;

        [Tooltip("Reverse rotation direction")]
        [SerializeField] private bool reverseDirection = false;

        [Tooltip("Enable rotation (uncheck to pause)")]
        [SerializeField] private bool enableRotation = true;

        private void Update()
        {
            if (!enableRotation) return;

            // Calculate rotation amount for this frame
            float rotationAmount = rotationSpeed * Time.deltaTime;

            // Apply reverse direction if enabled
            if (reverseDirection)
            {
                rotationAmount = -rotationAmount;
            }

            // Build rotation vector based on selected axes
            Vector3 rotationVector = Vector3.zero;

            if (rotateX) rotationVector.x = rotationAmount;
            if (rotateY) rotationVector.y = rotationAmount;
            if (rotateZ) rotationVector.z = rotationAmount;

            // Apply rotation
            if (useLocalSpace)
            {
                transform.Rotate(rotationVector, Space.Self);
            }
            else
            {
                transform.Rotate(rotationVector, Space.World);
            }
        }

        /// <summary>
        /// Set rotation speed at runtime
        /// </summary>
        public void SetSpeed(float speed)
        {
            rotationSpeed = speed;
        }

        /// <summary>
        /// Enable or disable rotation
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            enableRotation = enabled;
        }

        /// <summary>
        /// Set rotation axis at runtime
        /// </summary>
        public void SetRotationAxis(bool x, bool y, bool z)
        {
            rotateX = x;
            rotateY = y;
            rotateZ = z;
        }

        /// <summary>
        /// Toggle reverse direction
        /// </summary>
        public void SetReverse(bool reverse)
        {
            reverseDirection = reverse;
        }
    }
}
