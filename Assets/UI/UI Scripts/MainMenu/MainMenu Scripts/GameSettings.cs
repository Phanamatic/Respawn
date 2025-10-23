using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using System.Collections;
using Game.Services;

namespace UI.Scripts
{
    public class GameSettings : MonoBehaviour
    {
        [Header("Video Settings")]
        [SerializeField] private Slider fullscreenSlider;
        [SerializeField] private Image fullscreenHandle; // Handle image for color change
        [SerializeField] private TextMeshProUGUI fullscreenText; // Shows "Fullscreen" or "Windowed"
        [SerializeField] private TextMeshProUGUI resolutionText; // Shows current resolution (e.g., "1920 x 1080")

        [SerializeField] private Slider vsyncSlider;
        [SerializeField] private Image vsyncHandle; // Handle image for color change

        [Header("Slider Colors")]
        [SerializeField] private Color sliderOffColor = new Color(0f, 0.58f, 0.59f); // #009398
        [SerializeField] private Color sliderOnColor = new Color(0.42f, 0.89f, 0.93f); // #6AE4EC

        [Header("Sound Settings")]
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private Image masterVolumeHandle;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Image musicVolumeHandle;
        [SerializeField] private Slider sfxVolumeSlider;
        [SerializeField] private Image sfxVolumeHandle;

        [Header("Accessibility Settings")]
        [SerializeField] private Slider mouseSensitivitySlider;
        [SerializeField] private Image mouseSensitivityHandle;

        [Header("Reset Settings")]
        [SerializeField] private Button resetSettingsButton;
        [SerializeField] private Image resetRadialFill; // Radial fill image for hold indicator
        [SerializeField] private TextMeshProUGUI resetButtonText;
        [SerializeField] private float resetHoldDuration = 3f;
        [SerializeField] private float freezeDuration = 0.1f; // Freeze effect duration
        [SerializeField] private float successMessageDuration = 2f; // How long to show success message

        private bool isHoldingReset = false;
        private float resetHoldTime = 0f;
        private Coroutine resetCoroutine;
        private bool isResetting = false; // Prevents interruption once reset starts

        private void Start()
        {
            SetupListeners();
            LoadSettings();
            UpdateResolutionText();
            SetupResetButton();
        }

        private void Update()
        {
            // Continuously update resolution text in case screen resolution changes
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
            // Load settings from JSON file
            var settings = SettingsSaveManager.LoadSettings();

            // Video - Fullscreen Slider
            if (fullscreenSlider != null)
            {
                fullscreenSlider.SetValueWithoutNotify(settings.fullscreen ? 1 : 0);
                Screen.fullScreen = settings.fullscreen;
                UpdateFullscreenText(settings.fullscreen);
                UpdateHandleColor(fullscreenHandle, settings.fullscreen);
            }

            // Video - VSync Slider
            if (vsyncSlider != null)
            {
                vsyncSlider.SetValueWithoutNotify(settings.vsync ? 1 : 0);
                QualitySettings.vSyncCount = settings.vsync ? 1 : 0;
                UpdateHandleColor(vsyncHandle, settings.vsync);
            }

            // Sound - Set AudioManager volumes from saved settings
            if (masterVolumeSlider != null)
            {
                AudioManager.Instance.SetMasterVolume(settings.masterVolume);
                masterVolumeSlider.SetValueWithoutNotify(settings.masterVolume);
                UpdateSliderHandleColor(masterVolumeHandle, settings.masterVolume);
            }

            if (musicVolumeSlider != null)
            {
                AudioManager.Instance.SetMusicVolume(settings.musicVolume);
                musicVolumeSlider.SetValueWithoutNotify(settings.musicVolume);
                UpdateSliderHandleColor(musicVolumeHandle, settings.musicVolume);
            }

            if (sfxVolumeSlider != null)
            {
                AudioManager.Instance.SetSFXVolume(settings.sfxVolume);
                sfxVolumeSlider.SetValueWithoutNotify(settings.sfxVolume);
                UpdateSliderHandleColor(sfxVolumeHandle, settings.sfxVolume);
            }

            // Accessibility
            if (mouseSensitivitySlider != null)
            {
                mouseSensitivitySlider.SetValueWithoutNotify(settings.mouseSensitivity);
                UpdateSliderHandleColor(mouseSensitivityHandle, settings.mouseSensitivity);
            }
        }

        private void SetupListeners()
        {
            // Video - Sliders with smooth snapping and click-to-toggle
            if (fullscreenSlider != null)
            {
                fullscreenSlider.onValueChanged.AddListener(OnFullscreenSliderChanged);
                AddClickToggleToSlider(fullscreenSlider);
            }

            if (vsyncSlider != null)
            {
                vsyncSlider.onValueChanged.AddListener(OnVSyncSliderChanged);
                AddClickToggleToSlider(vsyncSlider);
            }

            // Sound - Setup listeners with handle color updates
            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetMasterVolume(value);
                    UpdateSliderHandleColor(masterVolumeHandle, value);
                });

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetMusicVolume(value);
                    UpdateSliderHandleColor(musicVolumeHandle, value);
                });

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.AddListener((value) =>
                {
                    SetSFXVolume(value);
                    UpdateSliderHandleColor(sfxVolumeHandle, value);
                });

            // Accessibility
            if (mouseSensitivitySlider != null)
                mouseSensitivitySlider.onValueChanged.AddListener((value) =>
                {
                    SetMouseSensitivity(value);
                    UpdateSliderHandleColor(mouseSensitivityHandle, value);
                });
        }

        // Video Methods - Slider Handlers
        private void OnFullscreenSliderChanged(float value)
        {
            // Smooth snap to 0 or 1
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
            // Smooth snap to 0 or 1
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
            SaveCurrentSettings();
        }

        public void SetVSync(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;
            UpdateHandleColor(vsyncHandle, enabled);
            SaveCurrentSettings();
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
            // Lerp between off and on colors based on slider value (0-1)
            if (handleImage != null)
            {
                Color targetColor = Color.Lerp(sliderOffColor, sliderOnColor, sliderValue);
                handleImage.DOColor(targetColor, 0.2f).SetEase(Ease.OutCubic);
            }
        }

        // Sound Methods
        public void SetMasterVolume(float volume)
        {
            AudioManager.Instance.SetMasterVolume(volume);
            SaveCurrentSettings();
        }

        public void SetMusicVolume(float volume)
        {
            AudioManager.Instance.SetMusicVolume(volume);
            SaveCurrentSettings();
        }

        public void SetSFXVolume(float volume)
        {
            AudioManager.Instance.SetSFXVolume(volume);
            SaveCurrentSettings();
        }

        // Accessibility Methods
        public void SetMouseSensitivity(float sensitivity)
        {
            SaveCurrentSettings();
        }

        // Helper to save all current settings to JSON
        private void SaveCurrentSettings()
        {
            var settings = new SettingsSaveManager.PlayerSettings
            {
                fullscreen = Screen.fullScreen,
                vsync = QualitySettings.vSyncCount > 0,
                masterVolume = AudioManager.Instance.MasterVolume,
                musicVolume = AudioManager.Instance.MusicVolume,
                sfxVolume = AudioManager.Instance.SFXVolume,
                mouseSensitivity = mouseSensitivitySlider != null ? mouseSensitivitySlider.value : 1f
            };

            SettingsSaveManager.SaveSettings(settings);
        }

        // Public getters for other scripts to use
        public static float GetMasterVolume()
        {
            return AudioManager.Instance != null ? AudioManager.Instance.MasterVolume : SettingsSaveManager.LoadSettings().masterVolume;
        }

        public static float GetMusicVolume()
        {
            return AudioManager.Instance != null ? AudioManager.Instance.MusicVolume : SettingsSaveManager.LoadSettings().musicVolume;
        }

        public static float GetSFXVolume()
        {
            return AudioManager.Instance != null ? AudioManager.Instance.SFXVolume : SettingsSaveManager.LoadSettings().sfxVolume;
        }

        public static float GetMouseSensitivity()
        {
            return SettingsSaveManager.LoadSettings().mouseSensitivity;
        }

        // ===== CLICK-TO-TOGGLE FOR SLIDERS =====
        private void AddClickToggleToSlider(Slider slider)
        {
            // Toggle logic that will be shared
            UnityEngine.Events.UnityAction toggleAction = () =>
            {
                // Toggle between 0 and 1
                float newValue = slider.value >= 0.5f ? 0f : 1f;
                slider.value = newValue;
            };

            // Add a button component to make the entire slider background clickable
            var sliderButton = slider.gameObject.GetComponent<Button>();
            if (sliderButton == null)
            {
                sliderButton = slider.gameObject.AddComponent<Button>();
            }
            sliderButton.transition = UnityEngine.UI.Selectable.Transition.None;
            sliderButton.onClick.RemoveAllListeners();
            sliderButton.onClick.AddListener(toggleAction);

            // Find and add button to the handle so it's also clickable
            if (slider.handleRect != null)
            {
                var handleButton = slider.handleRect.GetComponent<Button>();
                if (handleButton == null)
                {
                    handleButton = slider.handleRect.gameObject.AddComponent<Button>();
                }
                handleButton.transition = UnityEngine.UI.Selectable.Transition.None;
                handleButton.onClick.RemoveAllListeners();
                handleButton.onClick.AddListener(toggleAction);
            }
        }

        // ===== RESET SETTINGS BUTTON =====
        private void SetupResetButton()
        {
            if (resetRadialFill != null)
            {
                // Only reset fill amount, let Unity Inspector handle fill type/method/direction
                resetRadialFill.fillAmount = 0f;
            }

            if (resetSettingsButton != null)
            {
                // Add event triggers for hold functionality
                var eventTrigger = resetSettingsButton.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (eventTrigger == null)
                {
                    eventTrigger = resetSettingsButton.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                }

                // Clear existing triggers to avoid duplicates
                eventTrigger.triggers.Clear();

                // Pointer Down - Start holding
                var pointerDown = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerDown.eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown;
                pointerDown.callback.AddListener((data) => {
                    Debug.Log("Reset button PRESSED - starting hold");
                    StartResetHold();
                });
                eventTrigger.triggers.Add(pointerDown);

                // Pointer Up - Stop holding
                var pointerUp = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerUp.eventID = UnityEngine.EventSystems.EventTriggerType.PointerUp;
                pointerUp.callback.AddListener((data) => {
                    Debug.Log("Reset button RELEASED - stopping hold");
                    StopResetHold();
                });
                eventTrigger.triggers.Add(pointerUp);

                // Pointer Exit - Stop holding (if they drag off)
                var pointerExit = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerExit.eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit;
                pointerExit.callback.AddListener((data) => {
                    Debug.Log("Reset button EXITED - stopping hold");
                    StopResetHold();
                });
                eventTrigger.triggers.Add(pointerExit);
            }
            else
            {
                Debug.LogError("GameSettings: Reset Settings Button is not assigned!");
            }

            if (resetRadialFill == null)
            {
                Debug.LogError("GameSettings: Reset Radial Fill Image is not assigned!");
            }

            if (resetButtonText == null)
            {
                Debug.LogError("GameSettings: Reset Button Text is not assigned!");
            }
        }

        private void StartResetHold()
        {
            Debug.Log("StartResetHold called");
            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }
            resetCoroutine = StartCoroutine(ResetHoldCoroutine());
        }

        private void StopResetHold()
        {
            Debug.Log("StopResetHold called");

            // Don't interrupt if we're already resetting!
            if (isResetting)
            {
                Debug.Log("Reset in progress - cannot interrupt!");
                return;
            }

            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
                resetCoroutine = null;
            }

            isHoldingReset = false;
            resetHoldTime = 0f;

            // Reset radial fill
            if (resetRadialFill != null)
            {
                resetRadialFill.DOFillAmount(0f, 0.2f);
            }
        }

        private IEnumerator ResetHoldCoroutine()
        {
            Debug.Log("ResetHoldCoroutine started");
            isHoldingReset = true;
            resetHoldTime = 0f;

            // This part CAN be cancelled by releasing the button
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

            // Hold complete - NOW lock it and start the actual reset (cannot be cancelled)
            Debug.Log("Hold complete! Starting reset...");
            isResetting = true; // Lock BEFORE starting reset
            yield return StartCoroutine(ResetAllSettings());
        }

        private IEnumerator ResetAllSettings()
        {
            Debug.Log("ResetAllSettings started");
            // isResetting is already set to true in ResetHoldCoroutine - no need to set it again

            // Blink white effect
            if (resetRadialFill != null)
            {
                Color originalColor = resetRadialFill.color;
                resetRadialFill.DOColor(Color.white, 0.1f).SetLoops(4, LoopType.Yoyo);
                Debug.Log("Blink effect started");
            }

            yield return new WaitForSecondsRealtime(0.4f);

            // Freeze effect (customizable duration)
            Debug.Log($"Freezing for {freezeDuration} seconds");
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(freezeDuration);
            Time.timeScale = 1f; // ALWAYS unfreeze
            Debug.Log("Unfrozen");

            // Actually reset all settings - delete JSON file and load defaults
            Debug.Log("Resetting settings to defaults");
            SettingsSaveManager.ResetSettings();

            // Reload settings from the newly created defaults
            LoadSettings();

            // Reset radial
            if (resetRadialFill != null)
            {
                resetRadialFill.fillAmount = 0f;
            }

            // Change text to success message
            if (resetButtonText != null)
            {
                resetButtonText.text = "All Settings Reset";
                Debug.Log("Text changed to: All Settings Reset");
            }

            // Show success message for customizable duration
            Debug.Log($"Showing success message for {successMessageDuration} seconds");
            yield return new WaitForSecondsRealtime(successMessageDuration);

            // Reset text back to default (ready to be pressed again)
            if (resetButtonText != null)
            {
                resetButtonText.text = "Reset Settings";
                Debug.Log("Text changed back to: Reset Settings");
            }

            // Reset flags to allow reset again
            isHoldingReset = false;
            resetHoldTime = 0f;
            isResetting = false; // Unlock - reset is complete
            Debug.Log("Reset complete - button ready for next use");

            // Extra safety: ensure time is unfrozen
            Time.timeScale = 1f;
        }

        private void OnDestroy()
        {
            // Remove listeners
            if (fullscreenSlider != null)
                fullscreenSlider.onValueChanged.RemoveListener(OnFullscreenSliderChanged);

            if (vsyncSlider != null)
                vsyncSlider.onValueChanged.RemoveListener(OnVSyncSliderChanged);

            // Stop any running reset coroutine
            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }

            // Ensure time scale is reset
            Time.timeScale = 1f;

            // Note: Sound and accessibility sliders use lambda listeners,
            // they're automatically cleaned up by Unity
        }
    }
}