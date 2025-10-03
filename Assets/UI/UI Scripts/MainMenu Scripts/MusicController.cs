using UnityEngine;
using System.Collections;

namespace UI.Scripts
{
    /// <summary>
    /// Controls dynamic music volume swells and fades for the main menu.
    /// Attach this to a GameObject with an AudioSource component.
    /// The GameObject must be tagged as "Music" to work with AudioManager.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class MusicController : MonoBehaviour
    {
        [Header("Volume Swelling Settings")]
        [SerializeField] private bool enableDynamicVolume = true;
        [SerializeField] private float cycleInterval = 30f; // Time between volume changes
        [SerializeField] private float softVolumeMult = 0.6f; // Multiplier when soft
        [SerializeField] private float swellVolumeMult = 1.2f; // Multiplier when swelling
        [SerializeField] private float transitionDuration = 3f; // Time to transition between volumes

        [Header("Initial Settings")]
        [SerializeField] private float initialDelay = 0f; // Delay before first cycle

        private AudioSource _audioSource;
        private float _currentMultiplier = 1f;
        private Coroutine _volumeCycleCoroutine;
        private bool _isTransitioning = false;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();

            // Ensure this GameObject is tagged for AudioManager
            if (!CompareTag("Music"))
            {
                Debug.LogWarning($"MusicController on {gameObject.name} is not tagged as 'Music'. Tagging it now.");
                gameObject.tag = "Music";
            }
        }

        private void Start()
        {
            // Wait a frame to ensure AudioManager has initialized
            StartCoroutine(InitializeAfterFrame());
        }

        private IEnumerator InitializeAfterFrame()
        {
            yield return null;

            // Exclude this AudioSource from AudioManager's automatic control
            AudioManager.Instance.ExcludeFromAutoControl(_audioSource);

            // Set initial volume from AudioManager
            _audioSource.volume = AudioManager.Instance.GetEffectiveMusicVolume();

            // Start dynamic volume cycle if enabled
            if (enableDynamicVolume)
            {
                _volumeCycleCoroutine = StartCoroutine(VolumeCycleRoutine());
            }
        }

        private void OnEnable()
        {
            // Resume cycling if it was stopped
            if (enableDynamicVolume && _volumeCycleCoroutine == null)
            {
                _volumeCycleCoroutine = StartCoroutine(VolumeCycleRoutine());
            }
        }

        private void OnDisable()
        {
            // Stop cycling when disabled
            if (_volumeCycleCoroutine != null)
            {
                StopCoroutine(_volumeCycleCoroutine);
                _volumeCycleCoroutine = null;
            }
        }

        private void OnDestroy()
        {
            // Remove from exclusion list and reset volume
            if (AudioManager.Instance != null && _audioSource != null)
            {
                AudioManager.Instance.RemoveFromExclusion(_audioSource);
                _audioSource.volume = AudioManager.Instance.GetEffectiveMusicVolume();
            }
        }

        private IEnumerator VolumeCycleRoutine()
        {
            // Initial delay
            if (initialDelay > 0)
            {
                yield return new WaitForSeconds(initialDelay);
            }

            // Cycle: Normal -> Soft -> Swell -> Normal -> repeat
            while (true)
            {
                // Phase 1: Normal volume for cycleInterval
                yield return new WaitForSeconds(cycleInterval);

                // Phase 2: Transition to soft
                yield return TransitionToVolume(softVolumeMult);
                yield return new WaitForSeconds(cycleInterval);

                // Phase 3: Transition to swell
                yield return TransitionToVolume(swellVolumeMult);
                yield return new WaitForSeconds(cycleInterval);

                // Phase 4: Return to normal
                yield return TransitionToVolume(1f);
            }
        }

        private IEnumerator TransitionToVolume(float volumeMultiplier)
        {
            _isTransitioning = true;

            float baseVolume = AudioManager.Instance.GetEffectiveMusicVolume();
            float startVolume = _audioSource.volume;
            float targetVolume = baseVolume * volumeMultiplier;
            float elapsedTime = 0f;

            while (elapsedTime < transitionDuration)
            {
                elapsedTime += Time.deltaTime;
                float t = elapsedTime / transitionDuration;

                // Smooth transition using smoothstep
                t = t * t * (3f - 2f * t);

                // Recalculate base volume in case user changed it during transition
                baseVolume = AudioManager.Instance.GetEffectiveMusicVolume();
                targetVolume = baseVolume * volumeMultiplier;

                _audioSource.volume = Mathf.Lerp(startVolume, targetVolume, t);
                yield return null;
            }

            // Ensure final volume is set
            _currentMultiplier = volumeMultiplier;
            _audioSource.volume = targetVolume;

            _isTransitioning = false;
        }

        private void Update()
        {
            // Continuously apply current multiplier to respect user volume changes
            // This ensures slider changes are always reflected immediately
            if (_audioSource != null && !_isTransitioning)
            {
                float targetVolume = AudioManager.Instance.GetEffectiveMusicVolume() * _currentMultiplier;
                _audioSource.volume = targetVolume;
            }
        }

        /// <summary>
        /// Enables or disables dynamic volume cycling.
        /// </summary>
        public void SetDynamicVolumeEnabled(bool enabled)
        {
            enableDynamicVolume = enabled;

            if (enabled && _volumeCycleCoroutine == null && gameObject.activeInHierarchy)
            {
                _volumeCycleCoroutine = StartCoroutine(VolumeCycleRoutine());
            }
            else if (!enabled && _volumeCycleCoroutine != null)
            {
                StopCoroutine(_volumeCycleCoroutine);
                _volumeCycleCoroutine = null;

                // Return this audio source to normal volume
                _audioSource.volume = AudioManager.Instance.GetEffectiveMusicVolume();
            }
        }
    }
}
