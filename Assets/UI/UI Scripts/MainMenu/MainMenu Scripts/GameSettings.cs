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

        [Header("Keybinds Panel Transition")]
        [SerializeField] private Button editKeybindsButton;
        [SerializeField] private Button backFromKeybindsButton;
        [SerializeField] private RectTransform settingsPanel; // The main settings panel
        [SerializeField] private RectTransform keybindsPanel; // The keybinds edit panel
        [SerializeField] private float panelSlideDuration = 0.3f;
        [SerializeField] private Ease slideInEase = Ease.OutCubic;
        [SerializeField] private Ease slideOutEase = Ease.InCubic;

        [Header("Reset Settings")]
        [SerializeField] private Button resetSettingsButton;
        [SerializeField] private Image resetRadialFill; // Radial fill image for hold indicator
        [SerializeField] private TextMeshProUGUI resetButtonText;
        [SerializeField] private float resetHoldDuration = 3f;

        private bool isHoldingReset = false;
        private float resetHoldTime = 0f;
        private Coroutine resetCoroutine;

        private Vector2 settingsVisiblePos;
        private Vector2 settingsHiddenLeftPos;
        private Vector2 keybindsVisiblePos;
        private Vector2 keybindsHiddenRightPos;
        private bool keybindsPanelIsOpen = false;

        // PlayerPrefs keys
        private const string FULLSCREEN_KEY = "Fullscreen";
        private const string VSYNC_KEY = "VSync";
        private const string MASTER_VOLUME_KEY = "MasterVolume";
        private const string MUSIC_VOLUME_KEY = "MusicVolume";
        private const string SFX_VOLUME_KEY = "SFXVolume";
        private const string MOUSE_SENSITIVITY_KEY = "MouseSensitivity";

        private void Start()
        {
            SetupListeners();
            LoadSettings();
            UpdateResolutionText();
            SetupKeybindsButtons();
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
            // Video - Fullscreen Slider
            if (fullscreenSlider != null)
            {
                bool isFullscreen = PlayerPrefs.GetInt(FULLSCREEN_KEY, Screen.fullScreen ? 1 : 0) == 1;
                fullscreenSlider.SetValueWithoutNotify(isFullscreen ? 1 : 0);
                Screen.fullScreen = isFullscreen;
                UpdateFullscreenText(isFullscreen);
                UpdateHandleColor(fullscreenHandle, isFullscreen);
            }

            // Video - VSync Slider
            if (vsyncSlider != null)
            {
                bool vsyncEnabled = PlayerPrefs.GetInt(VSYNC_KEY, QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;
                vsyncSlider.SetValueWithoutNotify(vsyncEnabled ? 1 : 0);
                QualitySettings.vSyncCount = vsyncEnabled ? 1 : 0;
                UpdateHandleColor(vsyncHandle, vsyncEnabled);
            }

            // Sound - Load from AudioManager (set without triggering listeners)
            if (masterVolumeSlider != null)
            {
                float masterVolume = AudioManager.Instance.MasterVolume;
                masterVolumeSlider.SetValueWithoutNotify(masterVolume);
                UpdateSliderHandleColor(masterVolumeHandle, masterVolume);
            }

            if (musicVolumeSlider != null)
            {
                float musicVolume = AudioManager.Instance.MusicVolume;
                musicVolumeSlider.SetValueWithoutNotify(musicVolume);
                UpdateSliderHandleColor(musicVolumeHandle, musicVolume);
            }

            if (sfxVolumeSlider != null)
            {
                float sfxVolume = AudioManager.Instance.SFXVolume;
                sfxVolumeSlider.SetValueWithoutNotify(sfxVolume);
                UpdateSliderHandleColor(sfxVolumeHandle, sfxVolume);
            }

            // Accessibility
            if (mouseSensitivitySlider != null)
            {
                float sensitivity = PlayerPrefs.GetFloat(MOUSE_SENSITIVITY_KEY, 1f);
                mouseSensitivitySlider.value = sensitivity;
                UpdateSliderHandleColor(mouseSensitivityHandle, sensitivity);
            }
        }

        private void SetupListeners()
        {
            // Video - Sliders with smooth snapping
            if (fullscreenSlider != null)
                fullscreenSlider.onValueChanged.AddListener(OnFullscreenSliderChanged);

            if (vsyncSlider != null)
                vsyncSlider.onValueChanged.AddListener(OnVSyncSliderChanged);

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
            PlayerPrefs.SetInt(FULLSCREEN_KEY, isFullscreen ? 1 : 0);
            PlayerPrefs.Save();

            UpdateFullscreenText(isFullscreen);
            UpdateHandleColor(fullscreenHandle, isFullscreen);
        }

        public void SetVSync(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;
            PlayerPrefs.SetInt(VSYNC_KEY, enabled ? 1 : 0);
            PlayerPrefs.Save();

            UpdateHandleColor(vsyncHandle, enabled);
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
        }

        public void SetMusicVolume(float volume)
        {
            AudioManager.Instance.SetMusicVolume(volume);
        }

        public void SetSFXVolume(float volume)
        {
            AudioManager.Instance.SetSFXVolume(volume);
        }

        // Accessibility Methods
        public void SetMouseSensitivity(float sensitivity)
        {
            PlayerPrefs.SetFloat(MOUSE_SENSITIVITY_KEY, sensitivity);
            PlayerPrefs.Save();
        }

        // Public getters for other scripts to use
        public static float GetMasterVolume()
        {
            return AudioManager.Instance.MasterVolume;
        }

        public static float GetMusicVolume()
        {
            return AudioManager.Instance.MusicVolume;
        }

        public static float GetSFXVolume()
        {
            return AudioManager.Instance.SFXVolume;
        }

        public static float GetMouseSensitivity()
        {
            return PlayerPrefs.GetFloat(MOUSE_SENSITIVITY_KEY, 1f);
        }

        // ===== KEYBINDS PANEL TRANSITION =====
        private void SetupKeybindsButtons()
        {
            if (editKeybindsButton != null)
            {
                editKeybindsButton.onClick.AddListener(OpenKeybindsPanel);
            }

            if (backFromKeybindsButton != null)
            {
                backFromKeybindsButton.onClick.AddListener(CloseKeybindsPanel);
            }

            // Calculate positions based on SlidingPanel logic
            if (settingsPanel != null)
            {
                // Capture the visible position (assuming settings panel starts visible)
                settingsVisiblePos = settingsPanel.anchoredPosition;
                // Hidden position to the left
                settingsHiddenLeftPos = new Vector2(-Screen.width - settingsPanel.rect.width, settingsVisiblePos.y);
            }

            if (keybindsPanel != null)
            {
                // Keybinds should have the SAME visible position as settings (they occupy the same space)
                keybindsVisiblePos = settingsPanel != null ? settingsPanel.anchoredPosition : keybindsPanel.anchoredPosition;
                // Hidden position to the right
                keybindsHiddenRightPos = new Vector2(Screen.width + keybindsPanel.rect.width, keybindsVisiblePos.y);

                // Start keybinds panel off-screen to the right
                keybindsPanel.anchoredPosition = keybindsHiddenRightPos;
                keybindsPanel.gameObject.SetActive(true); // Keep active but off-screen
            }
        }

        public void OpenKeybindsPanel()
        {
            // Prevent opening if already open
            if (keybindsPanelIsOpen) return;

            keybindsPanelIsOpen = true;

            // Kill any active animations
            if (settingsPanel != null) settingsPanel.DOKill();
            if (keybindsPanel != null) keybindsPanel.DOKill();

            // Settings panel slides OUT to the left (using slideOutEase)
            if (settingsPanel != null)
            {
                settingsPanel.DOAnchorPos(settingsHiddenLeftPos, panelSlideDuration).SetEase(slideOutEase);
            }

            // Keybinds panel slides IN from the right (using slideInEase)
            if (keybindsPanel != null)
            {
                keybindsPanel.gameObject.SetActive(true);
                keybindsPanel.anchoredPosition = keybindsHiddenRightPos; // Start off-screen right
                keybindsPanel.DOAnchorPos(keybindsVisiblePos, panelSlideDuration).SetEase(slideInEase);
            }
        }

        public void CloseKeybindsPanel()
        {
            // Prevent closing if not open
            if (!keybindsPanelIsOpen) return;

            keybindsPanelIsOpen = false;

            // Kill any active animations
            if (settingsPanel != null) settingsPanel.DOKill();
            if (keybindsPanel != null) keybindsPanel.DOKill();

            // Keybinds panel slides OUT to the right (using slideOutEase)
            if (keybindsPanel != null)
            {
                keybindsPanel.DOAnchorPos(keybindsHiddenRightPos, panelSlideDuration).SetEase(slideOutEase);
            }

            // Settings panel slides IN from the left (using slideInEase)
            if (settingsPanel != null)
            {
                settingsPanel.anchoredPosition = settingsHiddenLeftPos; // Start off-screen left
                settingsPanel.DOAnchorPos(settingsVisiblePos, panelSlideDuration).SetEase(slideInEase);
            }
        }

        // ===== RESET SETTINGS BUTTON =====
        private void SetupResetButton()
        {
            if (resetRadialFill != null)
            {
                resetRadialFill.fillAmount = 0f;
                resetRadialFill.type = Image.Type.Filled;
                resetRadialFill.fillMethod = Image.FillMethod.Radial360;
                resetRadialFill.fillClockwise = true;
            }

            if (resetSettingsButton != null)
            {
                // Add event triggers for hold functionality
                var eventTrigger = resetSettingsButton.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (eventTrigger == null)
                {
                    eventTrigger = resetSettingsButton.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                }

                // Pointer Down - Start holding
                var pointerDown = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerDown.eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown;
                pointerDown.callback.AddListener((data) => { StartResetHold(); });
                eventTrigger.triggers.Add(pointerDown);

                // Pointer Up - Stop holding
                var pointerUp = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerUp.eventID = UnityEngine.EventSystems.EventTriggerType.PointerUp;
                pointerUp.callback.AddListener((data) => { StopResetHold(); });
                eventTrigger.triggers.Add(pointerUp);

                // Pointer Exit - Stop holding (if they drag off)
                var pointerExit = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerExit.eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit;
                pointerExit.callback.AddListener((data) => { StopResetHold(); });
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

            // Hold complete - reset settings
            yield return StartCoroutine(ResetAllSettings());
        }

        private IEnumerator ResetAllSettings()
        {
            // Blink white effect
            if (resetRadialFill != null)
            {
                Color originalColor = resetRadialFill.color;
                resetRadialFill.DOColor(Color.white, 0.1f).SetLoops(4, LoopType.Yoyo);
            }

            yield return new WaitForSeconds(0.4f);

            // Change text
            if (resetButtonText != null)
            {
                resetButtonText.text = "All Settings Reset";
            }

            // Freeze effect (Time.timeScale)
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(1f);
            Time.timeScale = 1f;

            // Actually reset all settings
            PlayerPrefs.DeleteAll();
            PlayerPrefs.Save();

            // Reload settings
            LoadSettings();

            // Reset radial
            if (resetRadialFill != null)
            {
                resetRadialFill.fillAmount = 0f;
            }

            // Reset text after delay
            yield return new WaitForSeconds(2f);
            if (resetButtonText != null)
            {
                resetButtonText.text = "Reset Settings";
            }

            isHoldingReset = false;
            resetHoldTime = 0f;
        }

        private void OnDestroy()
        {
            // Remove listeners
            if (fullscreenSlider != null)
                fullscreenSlider.onValueChanged.RemoveListener(OnFullscreenSliderChanged);

            if (vsyncSlider != null)
                vsyncSlider.onValueChanged.RemoveListener(OnVSyncSliderChanged);

            if (editKeybindsButton != null)
                editKeybindsButton.onClick.RemoveListener(OpenKeybindsPanel);

            if (backFromKeybindsButton != null)
                backFromKeybindsButton.onClick.RemoveListener(CloseKeybindsPanel);

            // Stop any running reset coroutine
            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }

            // Ensure time scale is reset
            Time.timeScale = 1f;

            // Kill any active panel animations
            if (settingsPanel != null)
                settingsPanel.DOKill();

            if (keybindsPanel != null)
                keybindsPanel.DOKill();

            // Note: Sound and accessibility sliders use lambda listeners,
            // they're automatically cleaned up by Unity
        }
    }
}