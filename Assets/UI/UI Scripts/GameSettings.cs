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
            LoadSettings();
            SetupListeners();
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

            // Sound
            if (masterVolumeSlider != null)
            {
                masterVolumeSlider.value = PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
                AudioListener.volume = masterVolumeSlider.value;
            }

            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.value = PlayerPrefs.GetFloat(MUSIC_VOLUME_KEY, 1f);
            }

            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.value = PlayerPrefs.GetFloat(SFX_VOLUME_KEY, 1f);
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

            // Sound
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
            AudioListener.volume = volume;
            PlayerPrefs.SetFloat(MASTER_VOLUME_KEY, volume);
            PlayerPrefs.Save();
        }

        public void SetMusicVolume(float volume)
        {
            PlayerPrefs.SetFloat(MUSIC_VOLUME_KEY, volume);
            PlayerPrefs.Save();
            // You can hook this up to your music AudioSource later
        }

        public void SetSFXVolume(float volume)
        {
            PlayerPrefs.SetFloat(SFX_VOLUME_KEY, volume);
            PlayerPrefs.Save();
            // You can hook this up to your SFX AudioSource later
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