using UnityEngine;

namespace Game.UI.MainMenu
{
    /// <summary>
    /// Smoothly moves camera between 4 random position points on X and Z axis
    /// Creates a cinematic background effect for main menu with easing and pauses
    /// </summary>
    public class ObjectRotator : MonoBehaviour
    {
        [Header("Position Points")]
        [Tooltip("Array of 4 Transform positions the camera will move between")]
        [SerializeField] private Transform[] positionPoints = new Transform[4];

        [Header("Movement Settings")]
        [Tooltip("Movement speed in units per second")]
        [SerializeField] private float moveSpeed = 3f;

        [Tooltip("How long to pause at each point (in seconds)")]
        [SerializeField] private float pauseDuration = 1f;

        [Header("Smoothing")]
        [Tooltip("Enable smooth ease-in and ease-out transitions")]
        [SerializeField] private bool useEasing = true;

        [Tooltip("Curve for smooth speed transitions (0 to 1)")]
        [SerializeField] private AnimationCurve easingCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Advanced Options")]
        [Tooltip("Enable movement (uncheck to pause)")]
        [SerializeField] private bool enableMovement = true;

        // Private state variables
        private Vector3 startPosition;
        private Vector3 targetPosition;
        private Quaternion lockedRotation;
        private int currentPointIndex = -1;
        private int previousPointIndex = -1;
        private float journeyLength;
        private float journeyTravelled;
        private bool isPaused = false;
        private float pauseTimer = 0f;

        private void Start()
        {
            // Validate position points
            if (positionPoints == null || positionPoints.Length != 4)
            {
                Debug.LogError("ObjectRotator requires exactly 4 position points!");
                enabled = false;
                return;
            }

            // Check for null points
            for (int i = 0; i < positionPoints.Length; i++)
            {
                if (positionPoints[i] == null)
                {
                    Debug.LogError($"Position point {i} is null! Please assign all 4 points.");
                    enabled = false;
                    return;
                }
            }

            // Lock the rotation at start
            lockedRotation = transform.rotation;

            // Select first random point
            SelectNewTarget();
        }

        private void Update()
        {
            if (!enableMovement) return;

            // Always enforce locked rotation
            transform.rotation = lockedRotation;

            // Handle pause state
            if (isPaused)
            {
                pauseTimer += Time.deltaTime;
                if (pauseTimer >= pauseDuration)
                {
                    isPaused = false;
                    pauseTimer = 0f;
                    SelectNewTarget();
                }
                return;
            }

            // Move towards target
            MoveTowardsTarget();
        }

        private void MoveTowardsTarget()
        {
            // Calculate distance to travel this frame
            float distanceThisFrame = moveSpeed * Time.deltaTime;
            journeyTravelled += distanceThisFrame;

            // Calculate progress (0 to 1)
            float progress = Mathf.Clamp01(journeyTravelled / journeyLength);

            // Apply easing if enabled
            float easedProgress = useEasing ? easingCurve.Evaluate(progress) : progress;

            // Move to new position
            transform.position = Vector3.Lerp(startPosition, targetPosition, easedProgress);

            // Check if we've reached the target
            if (progress >= 1f)
            {
                // Snap to exact target position
                transform.position = targetPosition;

                // Start pause
                isPaused = true;
            }
        }

        private void SelectNewTarget()
        {
            // Store current point as previous
            previousPointIndex = currentPointIndex;

            // Select a random point that's NOT the previous point
            int newPointIndex;
            do
            {
                newPointIndex = Random.Range(0, positionPoints.Length);
            }
            while (newPointIndex == previousPointIndex && positionPoints.Length > 1);

            currentPointIndex = newPointIndex;

            // Set up journey
            startPosition = transform.position;
            targetPosition = positionPoints[currentPointIndex].position;
            journeyLength = Vector3.Distance(startPosition, targetPosition);
            journeyTravelled = 0f;
        }

        /// <summary>
        /// Set movement speed at runtime
        /// </summary>
        public void SetSpeed(float speed)
        {
            moveSpeed = Mathf.Max(0.1f, speed);
        }

        /// <summary>
        /// Enable or disable movement
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            enableMovement = enabled;
        }

        /// <summary>
        /// Set pause duration at each point
        /// </summary>
        public void SetPauseDuration(float duration)
        {
            pauseDuration = Mathf.Max(0f, duration);
        }

        /// <summary>
        /// Toggle easing on/off
        /// </summary>
        public void SetEasing(bool enabled)
        {
            useEasing = enabled;
        }

        /// <summary>
        /// Immediately jump to a specific point index
        /// </summary>
        public void JumpToPoint(int pointIndex)
        {
            if (pointIndex >= 0 && pointIndex < positionPoints.Length)
            {
                transform.position = positionPoints[pointIndex].position;
                currentPointIndex = pointIndex;
                isPaused = true;
                pauseTimer = 0f;
            }
        }
    }
}
