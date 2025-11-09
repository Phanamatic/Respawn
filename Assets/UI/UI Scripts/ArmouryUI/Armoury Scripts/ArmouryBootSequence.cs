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

        [Header("Parent Panel & Children")]
        [Tooltip("Parent armoury panel whose children will scale up after boot")]
        [SerializeField] Transform armouryParentPanel;

        [Tooltip("Delay between each child scaling up (seconds)")]
        [SerializeField, Range(0.01f, 0.5f)] float childScaleDelay = 0.05f;

        [Header("Boot Text Destination")]
        [Tooltip("The text object panel that scales up first")]
        [SerializeField] GameObject bootTextDestinationPanel;

        [Tooltip("Sound effect to play when destination panel appears")]
        [SerializeField] AudioClip bootCompleteSound;

        [Tooltip("Volume of the boot complete sound (0-1)")]
        [SerializeField, Range(0f, 1f)] float bootSoundVolume = 1f;

        [Tooltip("Number of times to blink the destination panel")]
        [SerializeField, Range(1, 5)] int blinkCount = 2;

        [Tooltip("Duration of each blink")]
        [SerializeField, Range(0.05f, 0.3f)] float blinkDuration = 0.1f;

        [Tooltip("Pause after destination panel before scaling other children")]
        [SerializeField, Range(0.1f, 1f)] float pauseAfterDestination = 0.3f;

        [Header("Audio Control")]
        [Tooltip("Mute all other audio sources while boot sound plays")]
        [SerializeField] bool muteAllOtherAudio = true;

        [Tooltip("Duration to keep other audio muted (should cover sound effect length)")]
        [SerializeField, Range(0.5f, 5f)] float muteDuration = 2f;

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
        private bool _hasRunThisSession = false;
        private bool _isRunning = false;

        // Store original scales and positions of children
        private System.Collections.Generic.Dictionary<Transform, Vector3> _originalChildScales = new System.Collections.Generic.Dictionary<Transform, Vector3>();
        private System.Collections.Generic.Dictionary<Transform, Vector3> _originalChildPositions = new System.Collections.Generic.Dictionary<Transform, Vector3>();

        // Audio source for playing sounds
        private AudioSource _audioSource;

        private void Update()
        {
            // Test button to trigger boot sequence
            if (Input.GetKeyDown(KeyCode.J))
            {
                TriggerBootSequence();
            }
        }

        private void OnEnable()
        {
            // Only run once per scene load, first time panel is enabled
            if (!_hasRunThisSession && !_isRunning)
            {
                StartCoroutine(BootSequence());
            }
            else if (_hasRunThisSession)
            {
                // If sequence already ran, ensure boot panel is inactive
                if (bootPanel) bootPanel.SetActive(false);
                if (bootText) bootText.gameObject.SetActive(false);

                // Ensure all children are scaled and positioned to their originals (including destination)
                if (armouryParentPanel)
                {
                    foreach (Transform child in armouryParentPanel)
                    {
                        if (child.gameObject != bootPanel)
                        {
                            if (_originalChildScales.ContainsKey(child))
                            {
                                child.localScale = _originalChildScales[child];
                            }
                            if (_originalChildPositions.ContainsKey(child))
                            {
                                child.localPosition = _originalChildPositions[child];
                            }
                        }
                    }
                }

                // Ensure destination panel is active and visible
                if (bootTextDestinationPanel)
                {
                    bootTextDestinationPanel.SetActive(true);
                }
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

            // SETUP: Scale all children of armoury parent to 0 INCLUDING destination panel
            if (armouryParentPanel)
            {
                foreach (Transform child in armouryParentPanel)
                {
                    if (child.gameObject != bootPanel)
                    {
                        child.localScale = Vector3.zero;
                    }
                }
            }

            // Ensure boot panel is active and visible
            if (bootPanel) bootPanel.SetActive(true);

            // Ensure boot text is active and visible
            if (bootText)
            {
                bootText.gameObject.SetActive(true);
                bootText.text = "";
            }

            // Ensure destination panel is ACTIVE but scaled to 0
            if (bootTextDestinationPanel) bootTextDestinationPanel.SetActive(true);

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

            // Small delay before animations
            yield return new WaitForSeconds(0.2f);

            // TV shut-off effect on boot panel (fast scale to zero)
            if (bootPanel)
            {
                yield return StartCoroutine(ScaleDownEffect(bootPanel.transform));
            }

            // Deactivate boot panel and boot text
            if (bootPanel) bootPanel.SetActive(false);
            if (bootText) bootText.gameObject.SetActive(false);

            // Activate final status text (stays on forever)
            if (finalStatusText)
            {
                finalStatusText.gameObject.SetActive(true);
            }

            // FIRST: Scale up destination panel with blink and sound
            if (bootTextDestinationPanel && armouryParentPanel)
            {
                yield return StartCoroutine(ScaleUpDestinationPanel());
            }

            // Pause before scaling other children
            yield return new WaitForSeconds(pauseAfterDestination);

            // THEN: IRON MAN STYLE scale up all OTHER children one by one (Y then X)
            if (armouryParentPanel)
            {
                yield return StartCoroutine(ScaleUpChildrenIronManStyle());
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

        private IEnumerator ScaleUpDestinationPanel()
        {
            if (!bootTextDestinationPanel) yield break;

            // Get the original scale for destination panel
            Vector3 originalScale = _originalChildScales.ContainsKey(bootTextDestinationPanel.transform)
                ? _originalChildScales[bootTextDestinationPanel.transform]
                : Vector3.one;

            Vector3 originalPosition = _originalChildPositions.ContainsKey(bootTextDestinationPanel.transform)
                ? _originalChildPositions[bootTextDestinationPanel.transform]
                : bootTextDestinationPanel.transform.localPosition;

            // Ensure position is correct
            bootTextDestinationPanel.transform.localPosition = originalPosition;

            // Scale up Y first (vertical stretch)
            float elapsed = 0f;
            float duration = 0.15f; // Slightly slower than other children

            Vector3 startScale = Vector3.zero;
            Vector3 midScale = new Vector3(0, originalScale.y, originalScale.z);

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                bootTextDestinationPanel.transform.localScale = Vector3.Lerp(startScale, midScale, t);
                yield return null;
            }

            bootTextDestinationPanel.transform.localScale = midScale;

            // Then scale X (horizontal stretch)
            elapsed = 0f;
            startScale = bootTextDestinationPanel.transform.localScale;
            Vector3 endScale = originalScale;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                bootTextDestinationPanel.transform.localScale = Vector3.Lerp(startScale, endScale, t);
                yield return null;
            }

            bootTextDestinationPanel.transform.localScale = originalScale;
            bootTextDestinationPanel.transform.localPosition = originalPosition;

            // Play sound effect with muting
            if (bootCompleteSound && _audioSource)
            {
                // Mute all other audio if enabled
                if (muteAllOtherAudio)
                {
                    StartCoroutine(MuteAllOtherAudio());
                }

                _audioSource.PlayOneShot(bootCompleteSound, bootSoundVolume);
            }

            // Blink effect
            CanvasGroup canvasGroup = bootTextDestinationPanel.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = bootTextDestinationPanel.AddComponent<CanvasGroup>();
            }

            for (int i = 0; i < blinkCount; i++)
            {
                // Fade out
                canvasGroup.alpha = 0f;
                yield return new WaitForSeconds(blinkDuration);

                // Fade in
                canvasGroup.alpha = 1f;
                yield return new WaitForSeconds(blinkDuration);
            }

            // Ensure fully visible
            canvasGroup.alpha = 1f;
        }

        private IEnumerator ScaleUpChildrenIronManStyle()
        {
            if (!armouryParentPanel) yield break;

            foreach (Transform child in armouryParentPanel)
            {
                // Skip boot panel and destination panel
                if (child.gameObject == bootPanel || child.gameObject == bootTextDestinationPanel) continue;

                // Get the original scale for this child
                if (!_originalChildScales.ContainsKey(child))
                {
                    Debug.LogWarning($"[ArmouryBoot] {child.name} not found in original scales!");
                    continue;
                }

                Vector3 originalScale = _originalChildScales[child];
                Vector3 originalPosition = _originalChildPositions.ContainsKey(child) ? _originalChildPositions[child] : child.localPosition;

                // Ensure position is correct before scaling
                child.localPosition = originalPosition;

                Debug.Log($"[ArmouryBoot] Scaling up {child.name} to {originalScale}");

                // First: Scale Y to original (vertical stretch)
                float elapsed = 0f;
                float duration = 0.15f; // Slightly slower animation

                Vector3 startScale = child.localScale; // Should be (0, 0, originalScale.z)
                Vector3 midScale = new Vector3(0, originalScale.y, originalScale.z);

                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / duration;
                    child.localScale = Vector3.Lerp(startScale, midScale, t);
                    yield return null;
                }

                child.localScale = midScale;

                // Then: Scale X to original (horizontal stretch)
                elapsed = 0f;
                startScale = child.localScale;
                Vector3 endScale = originalScale;

                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / duration;
                    child.localScale = Vector3.Lerp(startScale, endScale, t);
                    yield return null;
                }

                child.localScale = originalScale;

                // Ensure position is still correct after scaling
                child.localPosition = originalPosition;

                Debug.Log($"[ArmouryBoot] {child.name} finished - Scale: {child.localScale}, Pos: {child.localPosition}");

                // Small delay before next child
                yield return new WaitForSeconds(childScaleDelay);
            }
        }

        private IEnumerator MuteAllOtherAudio()
        {
            // Find all audio sources in the scene
            AudioSource[] allAudioSources = FindObjectsOfType<AudioSource>();

            // Store which ones were unmuted so we can restore them
            System.Collections.Generic.List<AudioSource> previouslyUnmuted = new System.Collections.Generic.List<AudioSource>();

            // Mute all except our boot sound audio source
            foreach (AudioSource source in allAudioSources)
            {
                if (source != _audioSource && !source.mute)
                {
                    previouslyUnmuted.Add(source);
                    source.mute = true;
                }
            }

            // Wait for the mute duration
            yield return new WaitForSeconds(muteDuration);

            // Unmute all that were previously unmuted
            foreach (AudioSource source in previouslyUnmuted)
            {
                if (source != null) // Check if still exists
                {
                    source.mute = false;
                }
            }
        }

        // Public method to manually trigger the boot sequence
        public void TriggerBootSequence()
        {
            if (!_isRunning)
            {
                _hasRunThisSession = false;
                StartCoroutine(BootSequence());
            }
        }

        // Reset on scene load
        private void Awake()
        {
            // Reset the sequence flag every time the scene loads
            _hasRunThisSession = false;

            // Get or create AudioSource for sound effects
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
            }

            // Store original scales and positions of all children BEFORE any modifications
            _originalChildScales.Clear();
            _originalChildPositions.Clear();
            if (armouryParentPanel)
            {
                foreach (Transform child in armouryParentPanel)
                {
                    // Store ALL children including destination panel (but not boot panel)
                    if (child.gameObject != bootPanel)
                    {
                        _originalChildScales[child] = child.localScale;
                        _originalChildPositions[child] = child.localPosition;
                        Debug.Log($"[ArmouryBoot] Stored {child.name} - Scale: {child.localScale}, Pos: {child.localPosition}");
                    }
                }
            }

            // Ensure final status is hidden at start
            if (finalStatusText)
            {
                finalStatusText.gameObject.SetActive(false);
            }

            // Ensure destination panel is hidden at start (will be activated during boot)
            if (bootTextDestinationPanel)
            {
                bootTextDestinationPanel.SetActive(false);
            }

            // Reset boot panel scale
            if (bootPanel)
            {
                bootPanel.transform.localScale = Vector3.one;
            }

            // Ensure boot text is active at start
            if (bootText)
            {
                bootText.gameObject.SetActive(true);
            }
        }
    }
}
