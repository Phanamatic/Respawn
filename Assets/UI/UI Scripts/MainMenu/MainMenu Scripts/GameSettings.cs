using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using System.Collections;

namespace UI.Scripts
{
    public class GameSettings : MonoBehaviour
    {
        [Header("Video Settings")]
        [SerializeField] private Slider fullscreenSlider;
        [SerializeField] private Image fullscreenHandle;
        [SerializeField] private TextMeshProUGUI fullscreenText;
        [SerializeField] private TextMeshProUGUI resolutionText;

        [SerializeField] private Slider vsyncSlider;
        [SerializeField] private Image vsyncHandle;

        [Header("Slider Colors")]
        [SerializeField] private Color sliderOffColor = new Color(0f, 0.58f, 0.59f);
        [SerializeField] private Color sliderOnColor = new Color(0.42f, 0.89f, 0.93f);

        [Header("Sound Settings")]
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private Image masterVolumeHandle;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Image musicVolumeHandle;
        [SerializeField] private Slider sfxVolumeSlider;
        [SerializeField] private Image sfxVolumeHandle;

        [Header("Audio References")]
        [SerializeField] private AudioSource musicAudioSource; // Specific music source to control
        [SerializeField] private MusicController musicController; // Optional: Reference to disable when user adjusts

        [Header("Accessibility Settings")]
        [SerializeField] private Slider mouseSensitivitySlider;
        [SerializeField] private Image mouseSensitivityHandle;

        [Header("Reset Settings")]
        [SerializeField] private Button resetSettingsButton;
        [SerializeField] private Image resetRadialFill;
        [SerializeField] private TextMeshProUGUI resetButtonText;
        [SerializeField] private float resetHoldDuration = 3f;
        [SerializeField] private float freezeDuration = 0.1f;
        [SerializeField] private float successMessageDuration = 2f;

        // PlayerPrefs keys
        private const string FULLSCREEN_KEY = "Fullscreen";
        private const string VSYNC_KEY = "VSync";
        private const string MASTER_VOLUME_KEY = "MasterVolume";
        private const string MUSIC_VOLUME_KEY = "MusicVolume";
        private const string SFX_VOLUME_KEY = "SFXVolume";
        private const string MOUSE_SENSITIVITY_KEY = "MouseSensitivity";

        private bool isHoldingReset = false;
        private float resetHoldTime = 0f;
        private Coroutine resetCoroutine;
        private bool isResetting = false;
        private bool userAdjustedMusic = false; // Track if user has manually adjusted music

        private void Start()
        {
            SetupListeners();
            LoadSettings();
            UpdateResolutionText();
            SetupResetButton();
        }

        private void Update()
        {
            UpdateResolutionText();
        }

        private void UpdateResolutionText()
        {
            if (resolutionText != null)
            {
                resolutionText.text = $"{Screen.width} x {Screen.height}";
            }
        }

        private void LoadSettings()
        {
            // Load from PlayerPrefs with defaults
            bool fullscreen = PlayerPrefs.GetInt(FULLSCREEN_KEY, 1) == 1;
            bool vsync = PlayerPrefs.GetInt(VSYNC_KEY, 1) == 1;
            float masterVolume = PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
            float musicVolume = PlayerPrefs.GetFloat(MUSIC_VOLUME_KEY, 1f);
            float sfxVolume = PlayerPrefs.GetFloat(SFX_VOLUME_KEY, 1f);
            float mouseSensitivity = PlayerPrefs.GetFloat(MOUSE_SENSITIVITY_KEY, 1f);

            // Apply Video Settings
            if (fullscreenSlider != null)
            {
                fullscreenSlider.SetValueWithoutNotify(fullscreen ? 1 : 0);
                Screen.fullScreen = fullscreen;
                UpdateFullscreenText(fullscreen);
                UpdateHandleColor(fullscreenHandle, fullscreen);
            }

            if (vsyncSlider != null)
            {
                vsyncSlider.SetValueWithoutNotify(vsync ? 1 : 0);
                QualitySettings.vSyncCount = vsync ? 1 : 0;
                UpdateHandleColor(vsyncHandle, vsync);
            }

            // Apply Audio Settings
            if (masterVolumeSlider != null)
            {
                masterVolumeSlider.SetValueWithoutNotify(masterVolume);
                UpdateSliderHandleColor(masterVolumeHandle, masterVolume);
            }

            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.SetValueWithoutNotify(musicVolume);
                UpdateSliderHandleColor(musicVolumeHandle, musicVolume);
            }

            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.SetValueWithoutNotify(sfxVolume);
                UpdateSliderHandleColor(sfxVolumeHandle, sfxVolume);
            }

            // Apply master volume to all audio sources immediately
            ApplyMasterVolumeToAllAudioSources(masterVolume);
            ApplyMusicVolume(musicVolume, masterVolume);
            ApplySFXVolume(sfxVolume, masterVolume);

            // Enable/Disable MusicController based on saved music volume
            // If music volume is NOT at default (1.0), user has customized it, so disable MusicController
            if (musicController != null)
            {
                if (musicVolume == 1f)
                {
                    // Default volume - enable dynamic music
                    musicController.SetDynamicVolumeEnabled(true);
                    userAdjustedMusic = false;
                }
                else
                {
                    // User has customized - disable dynamic music
                    musicController.SetDynamicVolumeEnabled(false);
                    userAdjustedMusic = true;
                }
            }

            // Accessibility
            if (mouseSensitivitySlider != null)
            {
                mouseSensitivitySlider.SetValueWithoutNotify(mouseSensitivity);
                UpdateSliderHandleColor(mouseSensitivityHandle, mouseSensitivity);
            }
        }

        private void SetupListeners()
        {
            // Video - Sliders with click-to-toggle
            if (fullscreenSlider != null)
            {
                fullscreenSlider.onValueChanged.AddListener(OnFullscreenSliderChanged);
                AddClickToggleToSliderHandle(fullscreenSlider);
            }

            if (vsyncSlider != null)
            {
                vsyncSlider.onValueChanged.AddListener(OnVSyncSliderChanged);
                AddClickToggleToSliderHandle(vsyncSlider);
            }

            // Sound - Setup listeners with interaction fix
            if (masterVolumeSlider != null)
            {
                EnsureSliderInteractableAtZero(masterVolumeSlider);
                masterVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetMasterVolume(value);
                    UpdateSliderHandleColor(masterVolumeHandle, value);
                });
            }

            if (musicVolumeSlider != null)
            {
                EnsureSliderInteractableAtZero(musicVolumeSlider);
                musicVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetMusicVolume(value);
                    UpdateSliderHandleColor(musicVolumeHandle, value);
                });
            }

            if (sfxVolumeSlider != null)
            {
                EnsureSliderInteractableAtZero(sfxVolumeSlider);
                sfxVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetSFXVolume(value);
                    UpdateSliderHandleColor(sfxVolumeHandle, value);
                });
            }

            // Accessibility
            if (mouseSensitivitySlider != null)
            {
                EnsureSliderInteractableAtZero(mouseSensitivitySlider);
                mouseSensitivitySlider.onValueChanged.AddListener((value) =>
                {
                    SetMouseSensitivity(value);
                    UpdateSliderHandleColor(mouseSensitivityHandle, value);
                });
            }
        }

        // Fix slider interaction at low values by ensuring fill area is clickable
        private void EnsureSliderInteractableAtZero(Slider slider)
        {
            if (slider == null) return;

            // Make the entire slider background clickable, not just the handle
            var sliderTransform = slider.transform;

            // Add a larger clickable area to the fill area
            var fillArea = slider.fillRect?.parent?.GetComponent<RectTransform>();
            if (fillArea != null)
            {
                // Ensure the fill area extends to cover the full slider
                fillArea.anchorMin = new Vector2(0, 0);
                fillArea.anchorMax = new Vector2(1, 1);
                fillArea.offsetMin = Vector2.zero;
                fillArea.offsetMax = Vector2.zero;
            }

            // Make sure handle has reasonable minimum size
            if (slider.handleRect != null)
            {
                var handleImage = slider.handleRect.GetComponent<Image>();
                if (handleImage != null)
                {
                    // Ensure handle is always visible and clickable
                    var handleRectTransform = slider.handleRect;
                    handleRectTransform.sizeDelta = new Vector2(Mathf.Max(handleRectTransform.sizeDelta.x, 20f), handleRectTransform.sizeDelta.y);
                }
            }
        }

        // ===== VIDEO SETTINGS =====
        private void OnFullscreenSliderChanged(float value)
        {
            float targetValue = value >= 0.5f ? 1f : 0f;

            if (fullscreenSlider != null)
            {
                fullscreenSlider.DOValue(targetValue, 0.15f).SetEase(Ease.OutCubic);
            }

            bool isFullscreen = targetValue == 1f;
            SetFullscreen(isFullscreen);
        }

        private void OnVSyncSliderChanged(float value)
        {
            float targetValue = value >= 0.5f ? 1f : 0f;

            if (vsyncSlider != null)
            {
                vsyncSlider.DOValue(targetValue, 0.15f).SetEase(Ease.OutCubic);
            }

            bool enabled = targetValue == 1f;
            SetVSync(enabled);
        }

        public void SetFullscreen(bool isFullscreen)
        {
            Screen.fullScreen = isFullscreen;
            UpdateFullscreenText(isFullscreen);
            UpdateHandleColor(fullscreenHandle, isFullscreen);

            PlayerPrefs.SetInt(FULLSCREEN_KEY, isFullscreen ? 1 : 0);
            PlayerPrefs.Save();
        }

        public void SetVSync(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;
            UpdateHandleColor(vsyncHandle, enabled);

            PlayerPrefs.SetInt(VSYNC_KEY, enabled ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void UpdateFullscreenText(bool isFullscreen)
        {
            if (fullscreenText != null)
            {
                fullscreenText.text = isFullscreen ? "Fullscreen" : "Windowed";
            }
        }

        private void UpdateHandleColor(Image handleImage, bool isOn)
        {
            if (handleImage != null)
            {
                Color targetColor = isOn ? sliderOnColor : sliderOffColor;
                handleImage.DOColor(targetColor, 0.2f).SetEase(Ease.OutCubic);
            }
        }

        private void UpdateSliderHandleColor(Image handleImage, float sliderValue)
        {
            if (handleImage != null)
            {
                Color targetColor = Color.Lerp(sliderOffColor, sliderOnColor, sliderValue);
                handleImage.DOColor(targetColor, 0.2f).SetEase(Ease.OutCubic);
            }
        }

        // ===== AUDIO SETTINGS =====
        public void SetMasterVolume(float volume)
        {
            volume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(MASTER_VOLUME_KEY, volume);
            PlayerPrefs.Save();

            // Apply to ALL audio sources in the scene
            ApplyMasterVolumeToAllAudioSources(volume);
        }

        public void SetMusicVolume(float volume)
        {
            volume = Mathf.Clamp01(volume);

            // Disable MusicController FIRST (before anything else) to prevent Update() override
            if (musicController != null)
            {
                musicController.SetDynamicVolumeEnabled(false);
            }

            // Mark that user has adjusted music
            userAdjustedMusic = true;

            // Save to PlayerPrefs
            PlayerPrefs.SetFloat(MUSIC_VOLUME_KEY, volume);
            PlayerPrefs.Save();

            // Apply music volume
            float masterVolume = PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
            ApplyMusicVolume(volume, masterVolume);
        }

        public void SetSFXVolume(float volume)
        {
            volume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(SFX_VOLUME_KEY, volume);
            PlayerPrefs.Save();

            // Apply SFX volume to GlobalButtonSoundManager
            float masterVolume = PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
            ApplySFXVolume(volume, masterVolume);
        }

        private void ApplyMasterVolumeToAllAudioSources(float masterVolume)
        {
            // Find ALL audio sources in the scene and apply master volume
            AudioSource[] allAudioSources = FindObjectsOfType<AudioSource>(true);

            float musicVol = PlayerPrefs.GetFloat(MUSIC_VOLUME_KEY, 1f);
            float sfxVol = PlayerPrefs.GetFloat(SFX_VOLUME_KEY, 1f);

            foreach (AudioSource source in allAudioSources)
            {
                if (source == null) continue;

                try
                {
                    // Check if it's a music source
                    if (source.CompareTag("Music") || source == musicAudioSource)
                    {
                        source.volume = musicVol * masterVolume;
                    }
                    // Check if it's an SFX source
                    else if (source.CompareTag("SFX"))
                    {
                        source.volume = sfxVol * masterVolume;
                    }
                    // Otherwise apply master volume directly
                    else
                    {
                        source.volume = masterVolume;
                    }
                }
                catch (UnityException)
                {
                    // Tag doesn't exist, just apply master volume
                    source.volume = masterVolume;
                }
            }
        }

        private void ApplyMusicVolume(float musicVolume, float masterVolume)
        {
            // Apply to specific music source if assigned
            if (musicAudioSource != null)
            {
                musicAudioSource.volume = musicVolume * masterVolume;
            }

            // Also apply to all tagged music sources
            AudioSource[] allAudioSources = FindObjectsOfType<AudioSource>(true);
            foreach (AudioSource source in allAudioSources)
            {
                if (source != null)
                {
                    try
                    {
                        if (source.CompareTag("Music"))
                        {
                            source.volume = musicVolume * masterVolume;
                        }
                    }
                    catch (UnityException)
                    {
                        // Tag doesn't exist, skip
                    }
                }
            }
        }

        private void ApplySFXVolume(float sfxVolume, float masterVolume)
        {
            // Find and control GlobalButtonSoundManager
            GlobalButtonSoundManager buttonSoundManager = FindObjectOfType<GlobalButtonSoundManager>(true);
            if (buttonSoundManager != null)
            {
                buttonSoundManager.Volume = sfxVolume * masterVolume;
            }

            // Apply to all SFX-tagged audio sources
            AudioSource[] allAudioSources = FindObjectsOfType<AudioSource>(true);
            foreach (AudioSource source in allAudioSources)
            {
                if (source != null)
                {
                    try
                    {
                        if (source.CompareTag("SFX"))
                        {
                            source.volume = sfxVolume * masterVolume;
                        }
                    }
                    catch (UnityException)
                    {
                        // Tag doesn't exist, skip
                    }
                }
            }
        }

        // ===== ACCESSIBILITY =====
        public void SetMouseSensitivity(float sensitivity)
        {
            PlayerPrefs.SetFloat(MOUSE_SENSITIVITY_KEY, sensitivity);
            PlayerPrefs.Save();
        }

        // ===== CLICK-TO-TOGGLE FOR SLIDERS =====
        private void AddClickToggleToSliderHandle(Slider slider)
        {
            // Add click listener to the handle
            if (slider.handleRect != null)
            {
                var handleButton = slider.handleRect.GetComponent<Button>();
                if (handleButton == null)
                {
                    handleButton = slider.handleRect.gameObject.AddComponent<Button>();
                }
                handleButton.transition = Selectable.Transition.None;
                handleButton.onClick.RemoveAllListeners();
                handleButton.onClick.AddListener(() =>
                {
                    // Toggle between 0 and 1
                    float newValue = slider.value >= 0.5f ? 0f : 1f;
                    slider.value = newValue;
                });
            }
        }

        // ===== RESET SETTINGS =====
        private void SetupResetButton()
        {
            if (resetRadialFill != null)
            {
                resetRadialFill.fillAmount = 0f;
            }

            if (resetSettingsButton != null)
            {
                var eventTrigger = resetSettingsButton.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (eventTrigger == null)
                {
                    eventTrigger = resetSettingsButton.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                }

                eventTrigger.triggers.Clear();

                // Pointer Down
                var pointerDown = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerDown.eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown;
                pointerDown.callback.AddListener((data) => StartResetHold());
                eventTrigger.triggers.Add(pointerDown);

                // Pointer Up
                var pointerUp = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerUp.eventID = UnityEngine.EventSystems.EventTriggerType.PointerUp;
                pointerUp.callback.AddListener((data) => StopResetHold());
                eventTrigger.triggers.Add(pointerUp);

                // Pointer Exit
                var pointerExit = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerExit.eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit;
                pointerExit.callback.AddListener((data) => StopResetHold());
                eventTrigger.triggers.Add(pointerExit);
            }
        }

        private void StartResetHold()
        {
            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }
            resetCoroutine = StartCoroutine(ResetHoldCoroutine());
        }

        private void StopResetHold()
        {
            if (isResetting) return;

            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
                resetCoroutine = null;
            }

            isHoldingReset = false;
            resetHoldTime = 0f;

            if (resetRadialFill != null)
            {
                resetRadialFill.DOFillAmount(0f, 0.2f);
            }
        }

        private IEnumerator ResetHoldCoroutine()
        {
            isHoldingReset = true;
            resetHoldTime = 0f;

            while (resetHoldTime < resetHoldDuration)
            {
                resetHoldTime += Time.deltaTime;
                float fillAmount = resetHoldTime / resetHoldDuration;

                if (resetRadialFill != null)
                {
                    resetRadialFill.fillAmount = fillAmount;
                }

                yield return null;
            }

            isResetting = true;
            yield return StartCoroutine(ResetAllSettings());
        }

        private IEnumerator ResetAllSettings()
        {
            // Blink effect
            if (resetRadialFill != null)
            {
                resetRadialFill.DOColor(Color.white, 0.1f).SetLoops(4, LoopType.Yoyo);
            }

            yield return new WaitForSecondsRealtime(0.4f);

            // Freeze effect
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(freezeDuration);
            Time.timeScale = 1f;

            // Reset all PlayerPrefs
            PlayerPrefs.SetInt(FULLSCREEN_KEY, 1);
            PlayerPrefs.SetInt(VSYNC_KEY, 1);
            PlayerPrefs.SetFloat(MASTER_VOLUME_KEY, 1f);
            PlayerPrefs.SetFloat(MUSIC_VOLUME_KEY, 1f);
            PlayerPrefs.SetFloat(SFX_VOLUME_KEY, 1f);
            PlayerPrefs.SetFloat(MOUSE_SENSITIVITY_KEY, 1f);
            PlayerPrefs.Save();

            // Re-enable MusicController
            userAdjustedMusic = false;
            if (musicController != null)
            {
                musicController.SetDynamicVolumeEnabled(true);
            }

            // Reload settings
            LoadSettings();

            // Reset radial
            if (resetRadialFill != null)
            {
                resetRadialFill.fillAmount = 0f;
            }

            // Success message
            if (resetButtonText != null)
            {
                resetButtonText.text = "All Settings Reset";
            }

            yield return new WaitForSecondsRealtime(successMessageDuration);

            // Reset text
            if (resetButtonText != null)
            {
                resetButtonText.text = "Reset Settings";
            }

            isHoldingReset = false;
            resetHoldTime = 0f;
            isResetting = false;

            Time.timeScale = 1f;
        }

        private void OnDestroy()
        {
            // Remove listeners
            if (fullscreenSlider != null)
                fullscreenSlider.onValueChanged.RemoveListener(OnFullscreenSliderChanged);

            if (vsyncSlider != null)
                vsyncSlider.onValueChanged.RemoveListener(OnVSyncSliderChanged);

            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }

            Time.timeScale = 1f;
        }

        // ===== PUBLIC GETTERS =====
        public static float GetMasterVolume()
        {
            return PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
        }

        public static float GetMusicVolume()
        {
            return PlayerPrefs.GetFloat(MUSIC_VOLUME_KEY, 1f);
        }

        public static float GetSFXVolume()
        {
            return PlayerPrefs.GetFloat(SFX_VOLUME_KEY, 1f);
        }

        public static float GetMouseSensitivity()
        {
            return PlayerPrefs.GetFloat(MOUSE_SENSITIVITY_KEY, 1f);
        }
    }
}
