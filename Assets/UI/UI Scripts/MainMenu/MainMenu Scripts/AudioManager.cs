using UnityEngine;
using System.Collections.Generic;

namespace UI.Scripts
{
    /// <summary>
    /// Singleton AudioManager that persists across scenes and manages all audio.
    /// Controls Master, Music, and SFX volumes universally.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        private static AudioManager _instance;
        public static AudioManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("AudioManager");
                    _instance = go.AddComponent<AudioManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // PlayerPrefs keys
        private const string MASTER_VOLUME_KEY = "MasterVolume";
        private const string MUSIC_VOLUME_KEY = "MusicVolume";
        private const string SFX_VOLUME_KEY = "SFXVolume";

        private float _masterVolume = 1f;
        private float _musicVolume = 1f;
        private float _sfxVolume = 1f;

        // Cached AudioSources for performance
        private List<AudioSource> _musicSources = new List<AudioSource>();
        private List<AudioSource> _sfxSources = new List<AudioSource>();

        // AudioSources that should be excluded from automatic volume control (e.g., MusicController)
        private List<AudioSource> _excludedSources = new List<AudioSource>();

        public float MasterVolume => _masterVolume;
        public float MusicVolume => _musicVolume;
        public float SFXVolume => _sfxVolume;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            LoadVolumes();
        }

        private void Start()
        {
            // Find and register all audio sources in the scene
            RefreshAudioSources();

            // Continuously scan for new audio sources every frame
            InvokeRepeating(nameof(RefreshAudioSources), 1f, 0.5f);
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(RefreshAudioSources));

            // Clean up singleton reference
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            // Prevent instance from being recreated during shutdown
            CancelInvoke(nameof(RefreshAudioSources));
            _instance = null;
        }

        /// <summary>
        /// Finds all AudioSources in the scene and categorizes them by tag.
        /// Automatically called periodically to detect new audio sources.
        /// </summary>
        public void RefreshAudioSources()
        {
            // Find all GameObjects with Music tag (only if tag exists)
            try
            {
                GameObject[] musicObjects = GameObject.FindGameObjectsWithTag("Music");
                foreach (GameObject obj in musicObjects)
                {
                    AudioSource[] sources = obj.GetComponents<AudioSource>();
                    foreach (AudioSource source in sources)
                    {
                        if (!_musicSources.Contains(source))
                        {
                            _musicSources.Add(source);
                        }
                    }
                }
            }
            catch (UnityException)
            {
                // Tag doesn't exist yet, skip
            }

            // Find all GameObjects with SFX tag (only if tag exists)
            try
            {
                GameObject[] sfxObjects = GameObject.FindGameObjectsWithTag("SFX");
                foreach (GameObject obj in sfxObjects)
                {
                    AudioSource[] sources = obj.GetComponents<AudioSource>();
                    foreach (AudioSource source in sources)
                    {
                        if (!_sfxSources.Contains(source))
                        {
                            _sfxSources.Add(source);
                        }
                    }
                }
            }
            catch (UnityException)
            {
                // Tag doesn't exist yet, skip
            }

            // Clean up null references
            _musicSources.RemoveAll(source => source == null);
            _sfxSources.RemoveAll(source => source == null);

            // Apply current volumes to all sources
            ApplyVolumes();
        }

        /// <summary>
        /// Registers an AudioSource based on its GameObject's tag.
        /// Tags: "Music" for music sources, "SFX" for sound effects.
        /// </summary>
        public void RegisterAudioSource(AudioSource source)
        {
            if (source == null) return;

            if (source.CompareTag("Music"))
            {
                if (!_musicSources.Contains(source))
                {
                    _musicSources.Add(source);
                    source.volume = _musicVolume * _masterVolume;
                }
            }
            else if (source.CompareTag("SFX"))
            {
                if (!_sfxSources.Contains(source))
                {
                    _sfxSources.Add(source);
                    source.volume = _sfxVolume * _masterVolume;
                }
            }
        }

        /// <summary>
        /// Unregisters an AudioSource when it's destroyed.
        /// </summary>
        public void UnregisterAudioSource(AudioSource source)
        {
            _musicSources.Remove(source);
            _sfxSources.Remove(source);
        }

        private void LoadVolumes()
        {
            _masterVolume = PlayerPrefs.GetFloat(MASTER_VOLUME_KEY, 1f);
            _musicVolume = PlayerPrefs.GetFloat(MUSIC_VOLUME_KEY, 1f);
            _sfxVolume = PlayerPrefs.GetFloat(SFX_VOLUME_KEY, 1f);
        }

        public void SetMasterVolume(float volume)
        {
            _masterVolume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(MASTER_VOLUME_KEY, _masterVolume);
            PlayerPrefs.Save();
            ApplyVolumes();
        }

        public void SetMusicVolume(float volume)
        {
            _musicVolume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(MUSIC_VOLUME_KEY, _musicVolume);
            PlayerPrefs.Save();
            ApplyMusicVolume();
        }

        public void SetSFXVolume(float volume)
        {
            _sfxVolume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(SFX_VOLUME_KEY, _sfxVolume);
            PlayerPrefs.Save();
            ApplySFXVolume();
        }

        private void ApplyVolumes()
        {
            ApplyMusicVolume();
            ApplySFXVolume();
        }

        private void ApplyMusicVolume()
        {
            // Clean up null references
            _musicSources.RemoveAll(source => source == null);
            _excludedSources.RemoveAll(source => source == null);

            foreach (AudioSource source in _musicSources)
            {
                // Skip sources that are excluded (controlled by MusicController)
                if (source != null && !_excludedSources.Contains(source))
                {
                    source.volume = _musicVolume * _masterVolume;
                }
            }
        }

        private void ApplySFXVolume()
        {
            // Clean up null references
            _sfxSources.RemoveAll(source => source == null);

            foreach (AudioSource source in _sfxSources)
            {
                if (source != null)
                {
                    source.volume = _sfxVolume * _masterVolume;
                }
            }
        }

        /// <summary>
        /// Gets all currently registered music sources.
        /// </summary>
        public List<AudioSource> GetMusicSources()
        {
            _musicSources.RemoveAll(source => source == null);
            return new List<AudioSource>(_musicSources);
        }

        /// <summary>
        /// Gets all currently registered SFX sources.
        /// </summary>
        public List<AudioSource> GetSFXSources()
        {
            _sfxSources.RemoveAll(source => source == null);
            return new List<AudioSource>(_sfxSources);
        }

        /// <summary>
        /// Excludes an AudioSource from automatic volume control.
        /// Use this when a custom controller (like MusicController) manages the volume.
        /// </summary>
        public void ExcludeFromAutoControl(AudioSource source)
        {
            if (source != null && !_excludedSources.Contains(source))
            {
                _excludedSources.Add(source);
            }
        }

        /// <summary>
        /// Removes an AudioSource from the exclusion list.
        /// </summary>
        public void RemoveFromExclusion(AudioSource source)
        {
            _excludedSources.Remove(source);
        }

        /// <summary>
        /// Gets the effective volume for music (music * master).
        /// </summary>
        public float GetEffectiveMusicVolume()
        {
            return _musicVolume * _masterVolume;
        }

        /// <summary>
        /// Gets the effective volume for SFX (sfx * master).
        /// </summary>
        public float GetEffectiveSFXVolume()
        {
            return _sfxVolume * _masterVolume;
        }
    }
}
