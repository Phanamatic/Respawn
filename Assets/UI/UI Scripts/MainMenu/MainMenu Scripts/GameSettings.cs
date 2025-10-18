using UnityEngine;
using UnityEngine.UI;

namespace UI.Scripts
{
    public class GameSettings : MonoBehaviour
    {
        [Header("Video Settings")]
        [SerializeField] private Toggle fullscreenToggle;
        [SerializeField] private Toggle vsyncToggle;

        [Header("Sound Settings")]
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Slider sfxVolumeSlider;

        [Header("Accessibility Settings")]
        [SerializeField] private Slider mouseSensitivitySlider;
        [SerializeField] private Toggle invertYAxisToggle;

        // PlayerPrefs keys
        private const string FULLSCREEN_KEY = "Fullscreen";
        private const string VSYNC_KEY = "VSync";
        private const string MASTER_VOLUME_KEY = "MasterVolume";
        private const string MUSIC_VOLUME_KEY = "MusicVolume";
        private const string SFX_VOLUME_KEY = "SFXVolume";
        private const string MOUSE_SENSITIVITY_KEY = "MouseSensitivity";
        private const string INVERT_Y_KEY = "InvertY";

        private void Start()
        {
            SetupListeners();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Video
            if (fullscreenToggle != null)
            {
                fullscreenToggle.isOn = PlayerPrefs.GetInt(FULLSCREEN_KEY, Screen.fullScreen ? 1 : 0) == 1;
                Screen.fullScreen = fullscreenToggle.isOn;
            }

            if (vsyncToggle != null)
            {
                vsyncToggle.isOn = PlayerPrefs.GetInt(VSYNC_KEY, QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;
                QualitySettings.vSyncCount = vsyncToggle.isOn ? 1 : 0;
            }

            // Sound - Load from AudioManager (set without triggering listeners)
            if (masterVolumeSlider != null)
            {
                masterVolumeSlider.SetValueWithoutNotify(AudioManager.Instance.MasterVolume);
            }

            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.SetValueWithoutNotify(AudioManager.Instance.MusicVolume);
            }

            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.SetValueWithoutNotify(AudioManager.Instance.SFXVolume);
            }

            // Accessibility
            if (mouseSensitivitySlider != null)
            {
                mouseSensitivitySlider.value = PlayerPrefs.GetFloat(MOUSE_SENSITIVITY_KEY, 1f);
            }

            if (invertYAxisToggle != null)
            {
                invertYAxisToggle.isOn = PlayerPrefs.GetInt(INVERT_Y_KEY, 0) == 1;
            }
        }

        private void SetupListeners()
        {
            // Video
            if (fullscreenToggle != null)
                fullscreenToggle.onValueChanged.AddListener(SetFullscreen);

            if (vsyncToggle != null)
                vsyncToggle.onValueChanged.AddListener(SetVSync);

            // Sound - Setup listeners first
            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.AddListener(SetMasterVolume);

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.AddListener(SetMusicVolume);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.AddListener(SetSFXVolume);

            // Accessibility
            if (mouseSensitivitySlider != null)
                mouseSensitivitySlider.onValueChanged.AddListener(SetMouseSensitivity);

            if (invertYAxisToggle != null)
                invertYAxisToggle.onValueChanged.AddListener(SetInvertY);
        }

        // Video Methods
        public void SetFullscreen(bool isFullscreen)
        {
            Screen.fullScreen = isFullscreen;
            PlayerPrefs.SetInt(FULLSCREEN_KEY, isFullscreen ? 1 : 0);
            PlayerPrefs.Save();
        }

        public void SetVSync(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;
            PlayerPrefs.SetInt(VSYNC_KEY, enabled ? 1 : 0);
            PlayerPrefs.Save();
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

        public void SetInvertY(bool invert)
        {
            PlayerPrefs.SetInt(INVERT_Y_KEY, invert ? 1 : 0);
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

        public static bool GetInvertY()
        {
            return PlayerPrefs.GetInt(INVERT_Y_KEY, 0) == 1;
        }

        private void OnDestroy()
        {
            // Remove listeners
            if (fullscreenToggle != null)
                fullscreenToggle.onValueChanged.RemoveListener(SetFullscreen);

            if (vsyncToggle != null)
                vsyncToggle.onValueChanged.RemoveListener(SetVSync);

            if (masterVolumeSlider != null)
                masterVolumeSlider.onValueChanged.RemoveListener(SetMasterVolume);

            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.RemoveListener(SetMusicVolume);

            if (sfxVolumeSlider != null)
                sfxVolumeSlider.onValueChanged.RemoveListener(SetSFXVolume);

            if (mouseSensitivitySlider != null)
                mouseSensitivitySlider.onValueChanged.RemoveListener(SetMouseSensitivity);

            if (invertYAxisToggle != null)
                invertYAxisToggle.onValueChanged.RemoveListener(SetInvertY);
        }
    }
}