using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using System.Collections;

namespace Game.UI.MainMenu
{
    /// <summary>
    /// Adds visual effects to button text: pulse animation and shimmer effect
    /// Attach to any button to make its text more dynamic and eye-catching
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class ButtonTextEffects : MonoBehaviour
    {
        [Header("Text Reference")]
        [Tooltip("Text component to animate (auto-finds if not assigned)")]
        [SerializeField] private TextMeshProUGUI buttonText;

        [Header("Pulse Effect")]
        [Tooltip("Enable pulsing scale animation")]
        [SerializeField] private bool enablePulse = true;

        [Tooltip("Minimum scale multiplier (1 = normal size)")]
        [SerializeField] private float pulseMinScale = 1.0f;

        [Tooltip("Maximum scale multiplier (1.1 = 10% bigger)")]
        [SerializeField] private float pulseMaxScale = 1.1f;

        [Tooltip("Duration of one pulse cycle (seconds)")]
        [SerializeField] private float pulseDuration = 1.0f;

        [Tooltip("Ease curve for pulse animation")]
        [SerializeField] private Ease pulseEase = Ease.InOutSine;

        [Header("Shimmer Effect")]
        [Tooltip("Enable shimmer/shine effect")]
        [SerializeField] private bool enableShimmer = true;

        [Tooltip("Shimmer color (typically white or light color)")]
        [SerializeField] private Color shimmerColor = Color.white;

        [Tooltip("How long the shimmer takes to sweep across (seconds)")]
        [SerializeField] private float shimmerDuration = 1.5f;

        [Tooltip("Delay between shimmer cycles (seconds)")]
        [SerializeField] private float shimmerDelay = 3.0f;

        [Tooltip("Width of the shimmer effect (0-1)")]
        [Range(0.1f, 1.0f)]
        [SerializeField] private float shimmerWidth = 0.3f;

        [Header("Trigger Options")]
        [Tooltip("Play effects continuously")]
        [SerializeField] private bool playOnLoop = true;

        [Tooltip("Play effects on mouse hover")]
        [SerializeField] private bool playOnHover = false;

        private Vector3 originalScale;
        private Color originalColor;
        private Tween pulseTween;
        private Coroutine shimmerCoroutine;
        private Button button;
        private bool isHovering = false;

        private void Awake()
        {
            button = GetComponent<Button>();

            // Auto-find text if not assigned
            if (buttonText == null)
            {
                buttonText = GetComponentInChildren<TextMeshProUGUI>();
            }

            if (buttonText == null)
            {
                Debug.LogWarning($"ButtonTextEffects: No TextMeshProUGUI found on {gameObject.name}");
                enabled = false;
                return;
            }

            originalScale = buttonText.transform.localScale;
            originalColor = buttonText.color;
        }

        private void OnEnable()
        {
            if (playOnLoop)
            {
                StartEffects();
            }
        }

        private void OnDisable()
        {
            StopEffects();
        }

        private void StartEffects()
        {
            if (enablePulse)
            {
                StartPulse();
            }

            if (enableShimmer)
            {
                StartShimmer();
            }
        }

        private void StopEffects()
        {
            // Stop pulse
            if (pulseTween != null)
            {
                pulseTween.Kill();
                pulseTween = null;
            }

            // Stop shimmer
            if (shimmerCoroutine != null)
            {
                StopCoroutine(shimmerCoroutine);
                shimmerCoroutine = null;
            }

            // Reset to original state
            if (buttonText != null)
            {
                buttonText.transform.localScale = originalScale;
                buttonText.color = originalColor;
            }
        }

        private void StartPulse()
        {
            if (buttonText == null) return;

            // Kill any existing pulse
            if (pulseTween != null)
            {
                pulseTween.Kill();
            }

            // Create looping pulse animation
            pulseTween = buttonText.transform
                .DOScale(originalScale * pulseMaxScale, pulseDuration * 0.5f)
                .SetEase(pulseEase)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private void StartShimmer()
        {
            if (buttonText == null) return;

            // Stop any existing shimmer
            if (shimmerCoroutine != null)
            {
                StopCoroutine(shimmerCoroutine);
            }

            shimmerCoroutine = StartCoroutine(ShimmerLoop());
        }

        private IEnumerator ShimmerLoop()
        {
            // Enable TextMeshPro vertex color mode
            buttonText.enableVertexGradient = true;

            while (true)
            {
                // Wait for delay before next shimmer
                yield return new WaitForSeconds(shimmerDelay);

                // Perform shimmer sweep
                yield return ShimmerSweep();
            }
        }

        private IEnumerator ShimmerSweep()
        {
            float elapsed = 0f;

            while (elapsed < shimmerDuration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / shimmerDuration;

                // Calculate shimmer position (moves from left -1 to right 2)
                float shimmerPosition = Mathf.Lerp(-shimmerWidth, 1f + shimmerWidth, progress);

                // Create gradient based on shimmer position
                VertexGradient gradient = CalculateShimmerGradient(shimmerPosition);
                buttonText.colorGradient = gradient;

                yield return null;
            }

            // Reset to original color
            buttonText.enableVertexGradient = false;
            buttonText.color = originalColor;
        }

        private VertexGradient CalculateShimmerGradient(float shimmerPosition)
        {
            // Calculate if shimmer is hitting each corner of the text
            Color topLeft = CalculateShimmerColor(shimmerPosition, 0f);
            Color topRight = CalculateShimmerColor(shimmerPosition, 1f);
            Color bottomLeft = CalculateShimmerColor(shimmerPosition, 0f);
            Color bottomRight = CalculateShimmerColor(shimmerPosition, 1f);

            return new VertexGradient(topLeft, topRight, bottomLeft, bottomRight);
        }

        private Color CalculateShimmerColor(float shimmerPosition, float textPosition)
        {
            // Calculate distance from shimmer center
            float distance = Mathf.Abs(textPosition - shimmerPosition);

            // If within shimmer width, blend with shimmer color
            if (distance < shimmerWidth)
            {
                float intensity = 1f - (distance / shimmerWidth);
                intensity = Mathf.Pow(intensity, 2); // Square for sharper falloff
                return Color.Lerp(originalColor, shimmerColor, intensity);
            }

            return originalColor;
        }

        /// <summary>
        /// Trigger a single shimmer effect
        /// </summary>
        public void TriggerShimmer()
        {
            if (enableShimmer && buttonText != null)
            {
                if (shimmerCoroutine != null)
                {
                    StopCoroutine(shimmerCoroutine);
                }
                StartCoroutine(ShimmerSweep());
            }
        }

        /// <summary>
        /// Enable or disable pulse effect
        /// </summary>
        public void SetPulseEnabled(bool enabled)
        {
            enablePulse = enabled;

            if (enabled)
            {
                StartPulse();
            }
            else if (pulseTween != null)
            {
                pulseTween.Kill();
                if (buttonText != null)
                {
                    buttonText.transform.localScale = originalScale;
                }
            }
        }

        /// <summary>
        /// Enable or disable shimmer effect
        /// </summary>
        public void SetShimmerEnabled(bool enabled)
        {
            enableShimmer = enabled;

            if (enabled)
            {
                StartShimmer();
            }
            else if (shimmerCoroutine != null)
            {
                StopCoroutine(shimmerCoroutine);
                if (buttonText != null)
                {
                    buttonText.enableVertexGradient = false;
                    buttonText.color = originalColor;
                }
            }
        }

        /// <summary>
        /// Set pulse speed
        /// </summary>
        public void SetPulseSpeed(float duration)
        {
            pulseDuration = duration;
            if (enablePulse && pulseTween != null)
            {
                StartPulse(); // Restart with new duration
            }
        }

        /// <summary>
        /// Set shimmer speed
        /// </summary>
        public void SetShimmerSpeed(float duration)
        {
            shimmerDuration = duration;
        }

        private void OnDestroy()
        {
            // Clean up
            if (pulseTween != null)
            {
                pulseTween.Kill();
            }

            if (buttonText != null)
            {
                buttonText.DOKill();
            }
        }
    }
}
