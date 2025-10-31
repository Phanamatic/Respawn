using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace UI.Scripts
{
    /// <summary>
    /// Persists across all scenes and plays a sound whenever any button is clicked.
    /// </summary>
    public class GlobalButtonSoundManager : MonoBehaviour
    {
        [Header("Audio Settings")]
        [SerializeField] private AudioClip buttonClickSound;
        [SerializeField, Range(0f, 1f)] private float volume = 1f;

        private static GlobalButtonSoundManager instance;
        private float lastSoundTime = -1f;

        private void Awake()
        {
            // Singleton pattern - only one instance across all scenes
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            // Subscribe to scene loaded event
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            // Initial setup for buttons in the first scene
            StartCoroutine(DelayedButtonSetup());
        }

        private void OnDestroy()
        {
            // Unsubscribe from scene loaded event
            SceneManager.sceneLoaded -= OnSceneLoaded;
            StopListeningForButtonClicks();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Re-setup buttons when a new scene loads
            StartCoroutine(DelayedButtonSetup());
        }

        private IEnumerator DelayedButtonSetup()
        {
            // Wait one frame to ensure all UI is initialized
            yield return new WaitForEndOfFrame();

            StopListeningForButtonClicks();
            StartListeningForButtonClicks();
        }

        private void StartListeningForButtonClicks()
        {
            // Find all buttons in the scene and add listeners
            Button[] allButtons = FindObjectsOfType<Button>(true);
            foreach (Button button in allButtons)
            {
                // Remove listener first to avoid duplicates
                button.onClick.RemoveListener(PlayButtonSound);
                // Add listener
                button.onClick.AddListener(PlayButtonSound);
            }
        }

        private void StopListeningForButtonClicks()
        {
            // Remove listeners from all buttons
            Button[] allButtons = FindObjectsOfType<Button>(true);
            foreach (Button button in allButtons)
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(PlayButtonSound);
                }
            }
        }

        private void PlayButtonSound()
        {
            if (buttonClickSound == null)
            {
                return;
            }

            // Prevent duplicate sounds within 50ms
            float currentTime = Time.unscaledTime;
            if (currentTime - lastSoundTime < 0.05f)
            {
                return;
            }

            lastSoundTime = currentTime;

            // Create a temporary GameObject with an AudioSource
            GameObject soundObject = new GameObject("ButtonClickSound");
            AudioSource audioSource = soundObject.AddComponent<AudioSource>();

            // Configure the audio source
            audioSource.clip = buttonClickSound;
            audioSource.volume = volume;
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f; // 2D sound (not spatial)

            // Play the sound
            audioSource.Play();

            // Destroy the GameObject after the sound finishes playing
            Destroy(soundObject, buttonClickSound.length + 0.1f);
        }
    }
}
