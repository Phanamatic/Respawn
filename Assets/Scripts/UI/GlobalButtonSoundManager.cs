using System.Collections;
using System.Collections.Generic;
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

        [Header("Debug")]
        [SerializeField] private bool showDebugLogs = false;

        private static GlobalButtonSoundManager instance;
        private float lastSoundTime = -1f;
        private HashSet<Button> registeredButtons = new HashSet<Button>();

        // Public getter/setter for volume
        public float Volume
        {
            get => volume;
            set => volume = Mathf.Clamp01(value);
        }

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
            // Find all buttons in the scene (including inactive ones)
            Button[] allButtons = Resources.FindObjectsOfTypeAll<Button>();

            int newButtonsCount = 0;

            foreach (Button button in allButtons)
            {
                // Skip buttons that are part of prefabs (not in scene)
                if (button.gameObject.scene.name == null) continue;

                // Skip if already registered
                if (registeredButtons.Contains(button)) continue;

                // Add listener
                button.onClick.AddListener(PlayButtonSound);
                registeredButtons.Add(button);
                newButtonsCount++;

                if (showDebugLogs)
                {
                    Debug.Log($"[GlobalButtonSound] Registered button: {button.gameObject.name}");
                }
            }

            if (showDebugLogs)
            {
                Debug.Log($"[GlobalButtonSound] Registered {newButtonsCount} new buttons. Total: {registeredButtons.Count}");
            }
        }

        private void StopListeningForButtonClicks()
        {
            // Remove listeners from all registered buttons
            foreach (Button button in registeredButtons)
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(PlayButtonSound);
                }
            }

            registeredButtons.Clear();

            if (showDebugLogs)
            {
                Debug.Log("[GlobalButtonSound] Cleared all button listeners");
            }
        }

        private void PlayButtonSound()
        {
            if (buttonClickSound == null)
            {
                if (showDebugLogs)
                {
                    Debug.LogWarning("[GlobalButtonSound] No audio clip assigned!");
                }
                return;
            }

            // Prevent duplicate sounds within 50ms
            float currentTime = Time.unscaledTime;
            if (currentTime - lastSoundTime < 0.05f)
            {
                if (showDebugLogs)
                {
                    Debug.Log("[GlobalButtonSound] Sound blocked (too soon)");
                }
                return;
            }

            lastSoundTime = currentTime;

            if (showDebugLogs)
            {
                Debug.Log("[GlobalButtonSound] Playing button click sound");
            }

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
