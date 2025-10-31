using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

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
        }

        private void OnEnable()
        {
            // Subscribe to all button clicks globally
            if (EventSystem.current != null)
            {
                StartListeningForButtonClicks();
            }
        }

        private void OnDisable()
        {
            StopListeningForButtonClicks();
        }

        private void Update()
        {
            // Detect button clicks via EventSystem
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                // Check if a button was clicked this frame
                if (Input.GetMouseButtonDown(0))
                {
                    GameObject clicked = EventSystem.current.currentSelectedGameObject;
                    Button button = clicked.GetComponent<Button>();

                    if (button != null && button.interactable)
                    {
                        PlayButtonSound();
                    }
                }
            }
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

        // Call this when a new scene is loaded to refresh button listeners
        private void OnLevelWasLoaded(int level)
        {
            // Re-subscribe to all buttons in the new scene
            StopListeningForButtonClicks();
            StartListeningForButtonClicks();
        }
    }
}
