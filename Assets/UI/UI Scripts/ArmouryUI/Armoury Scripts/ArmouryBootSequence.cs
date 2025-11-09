using UnityEngine;
using TMPro;
using System.Collections;

namespace UI.Scripts
{
    /// <summary>
    /// Plays a boot-up sequence animation when the Armoury Panel is first opened after scene load.
    /// Only runs once per scene session.
    /// Attach this to the Armoury Panel GameObject.
    /// </summary>
    public class ArmouryBootSequence : MonoBehaviour
    {
        [Header("Boot Panel (pulses and scales down)")]
        [SerializeField] GameObject bootPanel;
        [SerializeField] CanvasGroup bootPanelCanvasGroup;
        [SerializeField] TMP_Text bootText;

        [Header("Final Status Display")]
        [Tooltip("TextMeshPro that appears after boot sequence. Stays active once enabled.")]
        [SerializeField] TMP_Text finalStatusText;

        [Header("Animation Settings")]
        [SerializeField] string initMessage = "Initiating Armoury Systems...";
        [SerializeField] string onlineMessage = "[ARMOURY ONLINE]";

        [Tooltip("Characters typed per second")]
        [SerializeField, Range(1f, 50f)] float typingSpeed = 20f;

        [Tooltip("Delay between init message and online message (seconds)")]
        [SerializeField, Range(0.5f, 5f)] float messageDelay = 1.5f;

        [Header("Alpha Pulse Settings")]
        [SerializeField, Range(0f, 1f)] float minAlpha = 0.39f;  // 100/255
        [SerializeField, Range(0f, 1f)] float maxAlpha = 0.61f;  // 155/255
        [SerializeField] float pulseSpeed = 2f;

        [Header("Scale Down (TV Shut-off Effect)")]
        [SerializeField] float scaleDownDuration = 0.15f;

        // Track if sequence has run this scene session
        private static bool _hasRunThisSession = false;
        private bool _isRunning = false;

        private void OnEnable()
        {
            // Only run once per scene load, first time panel is enabled
            if (!_hasRunThisSession && !_isRunning)
            {
                StartCoroutine(BootSequence());
            }
        }

        private void OnDisable()
        {
            // Stop any running coroutines if panel is disabled
            StopAllCoroutines();
            _isRunning = false;
        }

        private IEnumerator BootSequence()
        {
            _isRunning = true;
            _hasRunThisSession = true;

            // Ensure boot panel is active
            if (bootPanel) bootPanel.SetActive(true);

            // Start alpha pulsing coroutine
            Coroutine pulseCoroutine = null;
            if (bootPanelCanvasGroup)
            {
                pulseCoroutine = StartCoroutine(PulseAlpha());
            }

            // Type out init message
            if (bootText)
            {
                yield return StartCoroutine(TypeText(bootText, initMessage));
            }

            // Wait between messages
            yield return new WaitForSeconds(messageDelay);

            // Type out online message
            if (bootText)
            {
                yield return StartCoroutine(TypeText(bootText, onlineMessage));
            }

            // Stop alpha pulsing
            if (pulseCoroutine != null)
            {
                StopCoroutine(pulseCoroutine);
            }

            // Small delay before scale down
            yield return new WaitForSeconds(0.2f);

            // TV shut-off effect (fast scale to zero)
            if (bootPanel)
            {
                yield return StartCoroutine(ScaleDownEffect(bootPanel.transform));
            }

            // Deactivate boot panel
            if (bootPanel) bootPanel.SetActive(false);

            // Activate final status text (stays on forever)
            if (finalStatusText)
            {
                finalStatusText.gameObject.SetActive(true);
            }

            _isRunning = false;
        }

        private IEnumerator TypeText(TMP_Text textComponent, string message)
        {
            textComponent.text = "";
            float delay = 1f / typingSpeed;

            foreach (char c in message)
            {
                textComponent.text += c;
                yield return new WaitForSeconds(delay);
            }
        }

        private IEnumerator PulseAlpha()
        {
            if (!bootPanelCanvasGroup) yield break;

            float time = 0f;
            while (true)
            {
                time += Time.deltaTime * pulseSpeed;
                float alpha = Mathf.Lerp(minAlpha, maxAlpha, (Mathf.Sin(time) + 1f) / 2f);
                bootPanelCanvasGroup.alpha = alpha;
                yield return null;
            }
        }

        private IEnumerator ScaleDownEffect(Transform target)
        {
            Vector3 startScale = target.localScale;
            Vector3 endScale = Vector3.zero;
            float elapsed = 0f;

            while (elapsed < scaleDownDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / scaleDownDuration;
                target.localScale = Vector3.Lerp(startScale, endScale, t);
                yield return null;
            }

            target.localScale = Vector3.zero;
        }

        // Call this if you need to reset the sequence (e.g., when changing scenes)
        public static void ResetSequence()
        {
            _hasRunThisSession = false;
        }

        // Reset on scene load
        private void Awake()
        {
            // Ensure final status is hidden at start
            if (finalStatusText)
            {
                finalStatusText.gameObject.SetActive(false);
            }
        }

#if UNITY_EDITOR
        // Reset in editor when exiting play mode
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _hasRunThisSession = false;
        }
#endif
    }
}
