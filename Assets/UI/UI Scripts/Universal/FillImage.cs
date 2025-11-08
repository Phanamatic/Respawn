using UnityEngine;
using UnityEngine.UI;

namespace UI.Universal
{
    [RequireComponent(typeof(Image))]
    public class FillImage : MonoBehaviour
    {
        [Header("Fill Settings")]
        [Tooltip("Enable to make the fill amount loop continuously")]
        [SerializeField] private bool loop = true;

        [Tooltip("Speed of the fill animation (0 = slowest, 10 = fastest)")]
        [Range(0f, 10f)]
        [SerializeField] private float speed = 5f;

        [Header("Fill Behavior")]
        [Tooltip("Should the fill go from empty to full and back (PingPong) or just reset?")]
        [SerializeField] private bool pingPong = true;

        [Tooltip("Starting fill amount (0 to 1)")]
        [Range(0f, 1f)]
        [SerializeField] private float startFillAmount = 0f;

        private Image fillImage;
        private float currentFill;
        private bool fillIncreasing = true;

        private void Awake()
        {
            fillImage = GetComponent<Image>();

            // Ensure the image is set to Filled type
            if (fillImage.type != Image.Type.Filled)
            {
                Debug.LogWarning($"FillImage on {gameObject.name}: Image type is not set to 'Filled'. Changing to Filled mode.");
                fillImage.type = Image.Type.Filled;
            }

            currentFill = startFillAmount;
            fillImage.fillAmount = currentFill;
        }

        private void Update()
        {
            if (!loop)
                return;

            // Calculate fill speed based on the 0-10 range
            float fillSpeed = speed * 0.1f; // Converts to 0-1 range per second

            if (pingPong)
            {
                // PingPong mode: fill goes up and down
                if (fillIncreasing)
                {
                    currentFill += fillSpeed * Time.deltaTime;
                    if (currentFill >= 1f)
                    {
                        currentFill = 1f;
                        fillIncreasing = false;
                    }
                }
                else
                {
                    currentFill -= fillSpeed * Time.deltaTime;
                    if (currentFill <= 0f)
                    {
                        currentFill = 0f;
                        fillIncreasing = true;
                    }
                }
            }
            else
            {
                // Loop mode: fill goes up and resets
                currentFill += fillSpeed * Time.deltaTime;
                if (currentFill >= 1f)
                {
                    currentFill = 0f;
                }
            }

            fillImage.fillAmount = currentFill;
        }

        /// <summary>
        /// Set the fill amount manually (useful for non-looping fills)
        /// </summary>
        public void SetFillAmount(float amount)
        {
            currentFill = Mathf.Clamp01(amount);
            fillImage.fillAmount = currentFill;
        }

        /// <summary>
        /// Toggle looping on/off
        /// </summary>
        public void SetLooping(bool enabled)
        {
            loop = enabled;
        }

        /// <summary>
        /// Set the speed (0-10 range)
        /// </summary>
        public void SetSpeed(float newSpeed)
        {
            speed = Mathf.Clamp(newSpeed, 0f, 10f);
        }

        /// <summary>
        /// Reset the fill to start amount
        /// </summary>
        public void ResetFill()
        {
            currentFill = startFillAmount;
            fillImage.fillAmount = currentFill;
            fillIncreasing = true;
        }
    }
}
