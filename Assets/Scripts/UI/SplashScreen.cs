using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace UI.Scripts
{
    /// <summary>
    /// Simple splash screen that holds for a duration and scales up the logo/text before transitioning.
    /// </summary>
    public class SplashScreen : MonoBehaviour
    {
        [Header("Scene Settings")]
        [SerializeField] private string nextSceneName = "MainMenu";
        [SerializeField] private float displayDuration = 3f;

        [Header("Visual Elements (Assign One or Both)")]
        [SerializeField] private Image logoImage;
        [SerializeField] private TextMeshProUGUI logoText;

        [Header("Scale Animation")]
        [SerializeField] private float startScale = 0.5f;
        [SerializeField] private float endScale = 1.2f;
        [SerializeField] private AnimationCurve scaleCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Typewriter Effect")]
        [SerializeField] private bool enableTypewriter = true;
        [SerializeField] private string textToType = "Just Two Guys";
        [SerializeField] private float typeSpeed = 0.08f; // Time between each character
        [SerializeField] private AudioClip typeSound;
        [SerializeField, Range(0f, 1f)] private float typeSoundVolume = 0.5f;

        private void Start()
        {
            StartCoroutine(SplashSequence());
        }

        private IEnumerator SplashSequence()
        {
            // Set initial scale
            if (logoImage != null)
            {
                logoImage.transform.localScale = Vector3.one * startScale;
            }
            if (logoText != null)
            {
                logoText.transform.localScale = Vector3.one * startScale;

                // Clear text for typewriter effect
                if (enableTypewriter)
                {
                    logoText.text = "";
                }
            }

            // Start typewriter effect if enabled
            Coroutine typewriterCoroutine = null;
            if (enableTypewriter && logoText != null)
            {
                typewriterCoroutine = StartCoroutine(TypewriterEffect());
            }

            float elapsed = 0f;

            // Animate scale over the display duration
            while (elapsed < displayDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / displayDuration;
                float curveValue = scaleCurve.Evaluate(t);
                float currentScale = Mathf.Lerp(startScale, endScale, curveValue);

                if (logoImage != null)
                {
                    logoImage.transform.localScale = Vector3.one * currentScale;
                }
                if (logoText != null)
                {
                    logoText.transform.localScale = Vector3.one * currentScale;
                }

                yield return null;
            }

            // Ensure final scale is set
            if (logoImage != null)
            {
                logoImage.transform.localScale = Vector3.one * endScale;
            }
            if (logoText != null)
            {
                logoText.transform.localScale = Vector3.one * endScale;
            }

            // Wait for typewriter to finish if it's still running
            if (typewriterCoroutine != null)
            {
                yield return typewriterCoroutine;
            }

            // Load next scene
            LoadNextScene();
        }

        private IEnumerator TypewriterEffect()
        {
            if (logoText == null || string.IsNullOrEmpty(textToType))
            {
                yield break;
            }

            logoText.text = "";

            for (int i = 0; i < textToType.Length; i++)
            {
                logoText.text += textToType[i];

                // Play type sound for each character (skip spaces)
                if (typeSound != null && textToType[i] != ' ')
                {
                    PlayTypeSound();
                }

                yield return new WaitForSeconds(typeSpeed);
            }
        }

        private void PlayTypeSound()
        {
            if (typeSound == null) return;

            // Create a temporary GameObject with an AudioSource
            GameObject soundObject = new GameObject("TypeSound");
            AudioSource audioSource = soundObject.AddComponent<AudioSource>();

            // Configure the audio source
            audioSource.clip = typeSound;
            audioSource.volume = typeSoundVolume;
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f; // 2D sound

            // Play the sound
            audioSource.Play();

            // Destroy the GameObject after the sound finishes playing
            Destroy(soundObject, typeSound.length + 0.1f);
        }

        private void LoadNextScene()
        {
            if (string.IsNullOrEmpty(nextSceneName))
            {
                Debug.LogError("[SplashScreen] Next scene name is not set!");
                return;
            }

            SceneManager.LoadSceneAsync(nextSceneName);
        }
    }
}
