using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

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

        private void OnDestroy()
        {
            // Remove listeners
            if (fullscreenSlider != null)
                fullscreenSlider.onValueChanged.RemoveListener(OnFullscreenSliderChanged);

            if (vsyncSlider != null)
                vsyncSlider.onValueChanged.RemoveListener(OnVSyncSliderChanged);

            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.RemoveListener(SetMasterVolume);

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.RemoveListener(SetMusicVolume);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.RemoveListener(SetSFXVolume);

            // Note: Sound and accessibility sliders use lambda listeners,
            // they're automatically cleaned up by Unity
        }
    }
}