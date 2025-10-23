using System;
using System.IO;
using UnityEngine;

namespace Game.Services
{
    /// <summary>
    /// Local JSON settings storage with cloud backup.
    /// Saves settings to a local file for instant access, syncs to cloud on login/logout.
    /// </summary>
    public static class SettingsSaveManager
    {
        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, "settings.json");

        [Serializable]
        public struct PlayerSettings
        {
            public bool fullscreen;
            public bool vsync;
            public float masterVolume;
            public float musicVolume;
            public float sfxVolume;
            public float mouseSensitivity;
        }

        /// <summary>
        /// Loads settings from local JSON file. Returns default settings if file doesn't exist.
        /// </summary>
        public static PlayerSettings LoadSettings()
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    string json = File.ReadAllText(FilePath);
                    var settings = JsonUtility.FromJson<PlayerSettings>(json);
                    Debug.Log($"[SettingsSaveManager] Settings loaded from {FilePath}");
                    return settings;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SettingsSaveManager] Failed to load settings: {e.Message}. Using defaults.");
                    return GetDefaultSettings();
                }
            }
            else
            {
                Debug.Log("[SettingsSaveManager] No settings file found. Using defaults.");
                return GetDefaultSettings();
            }
        }

        /// <summary>
        /// Saves settings to local JSON file immediately.
        /// </summary>
        public static void SaveSettings(PlayerSettings settings)
        {
            try
            {
                string json = JsonUtility.ToJson(settings, true);
                File.WriteAllText(FilePath, json);
                Debug.Log($"[SettingsSaveManager] Settings saved to {FilePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsSaveManager] Failed to save settings: {e.Message}");
            }
        }

        /// <summary>
        /// Deletes the local settings file and returns default settings.
        /// Use this when user clicks "Reset All Settings".
        /// </summary>
        public static PlayerSettings ResetSettings()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                    Debug.Log("[SettingsSaveManager] Settings file deleted.");
                }

                var defaults = GetDefaultSettings();
                SaveSettings(defaults); // Save defaults to file
                return defaults;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsSaveManager] Failed to reset settings: {e.Message}");
                return GetDefaultSettings();
            }
        }

        /// <summary>
        /// Returns default settings values.
        /// </summary>
        public static PlayerSettings GetDefaultSettings()
        {
            return new PlayerSettings
            {
                fullscreen = Screen.fullScreen,
                vsync = QualitySettings.vSyncCount > 0,
                masterVolume = 1f,
                musicVolume = 1f,
                sfxVolume = 1f,
                mouseSensitivity = 1f
            };
        }

        /// <summary>
        /// Checks if settings file exists locally.
        /// </summary>
        public static bool SettingsFileExists()
        {
            return File.Exists(FilePath);
        }
    }
}
