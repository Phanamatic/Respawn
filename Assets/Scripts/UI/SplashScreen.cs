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
        [SerializeField] private string nextSceneName = "Account";

        [Header("Logo")]
        [SerializeField] private Image logoImage;
        [SerializeField] private float logoSpinDuration = 1.2f;
        [SerializeField] private float logoStartScale = 0.1f; // 10% of original

        [Header("Text")]
        [SerializeField] private TextMeshProUGUI topLineText;
        [SerializeField] private TextMeshProUGUI bottomLineText;
        [SerializeField] private string topLine = "A game by";
        [SerializeField] private string bottomLine = "Just Two Guys";
        [SerializeField] private float characterInterval = 0.08f; // seconds per character

        [Header("Typing Sound")]
        [SerializeField] private AudioClip typingLoopClip;
        [SerializeField, Range(0f, 1f)] private float typingVolume = 0.5f;

        private AudioSource _typingSource;
        private Vector3 _logoOriginalScale = Vector3.one;
        private Quaternion _logoOriginalRotation = Quaternion.identity;

        private void Awake()
        {
            // Cache original logo transform and set starting state.
            if (logoImage != null)
            {
                var rt = logoImage.rectTransform;
                _logoOriginalScale = rt.localScale;
                _logoOriginalRotation = rt.localRotation;

                rt.localScale = _logoOriginalScale * logoStartScale;
                rt.localRotation = Quaternion.Euler(0f, 0f, 360f);
            }

            // Clear texts before we type.
            if (topLineText != null) topLineText.text = string.Empty;
            if (bottomLineText != null) bottomLineText.text = string.Empty;

            // Prepare looping typing audio.
            if (typingLoopClip != null)
            {
                _typingSource = gameObject.AddComponent<AudioSource>();
                _typingSource.clip = typingLoopClip;
                _typingSource.loop = true;
                _typingSource.playOnAwake = false;
                _typingSource.volume = typingVolume;
                _typingSource.spatialBlend = 0f; // 2D sound
            }
        }

        private void Start()
        {
            StartCoroutine(RunSplash());
        }

        private IEnumerator RunSplash()
        {
            // 1) Spin + scale logo.
            if (logoImage != null)
            {
                yield return StartCoroutine(AnimateLogo());
            }

            // 2) Type text with looping sound.
            yield return StartCoroutine(TypeTextSequence());

            // 3) As soon as the last letter is typed the sound stops (inside TypeTextSequence),
            //    then we wait 2 seconds before loading the next scene.
            yield return new WaitForSeconds(2f);

            LoadNextScene();
        }


// Flow is now: logo spin → type text → sound stops → 2s pause → next scene.

        private IEnumerator AnimateLogo()
        {
            RectTransform rt = logoImage.rectTransform;
            float duration = Mathf.Max(0.01f, logoSpinDuration);
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // Spin from 360° → 0° and scale from 0.1 → 1.0.
                float angle = Mathf.Lerp(360f, 0f, t);
                float scaleFactor = Mathf.Lerp(logoStartScale, 1f, t);

                rt.localRotation = Quaternion.Euler(0f, 0f, angle);
                rt.localScale = _logoOriginalScale * scaleFactor;

                yield return null;
            }

            // Snap to original transform.
            rt.localRotation = _logoOriginalRotation;
            rt.localScale = _logoOriginalScale;
        }

        private IEnumerator TypeTextSequence()
        {
            float wait = Mathf.Max(0.01f, characterInterval);
            bool startedAudio = false;

            // Top line: "A game by"
            if (topLineText != null && !string.IsNullOrEmpty(topLine))
            {
                topLineText.text = string.Empty;
                for (int i = 0; i < topLine.Length; i++)
                {
                    char c = topLine[i];
                    topLineText.text += c;

                    if (!startedAudio && !char.IsWhiteSpace(c))
                    {
                        StartTypingAudio();
                        startedAudio = true;
                    }

                    // Top line can still use the per-character delay, including the last char.
                    yield return new WaitForSeconds(wait);
                }
            }

            // Bottom line: "Just Two Guys"
            if (bottomLineText != null && !string.IsNullOrEmpty(bottomLine))
            {
                bottomLineText.text = string.Empty;
                for (int i = 0; i < bottomLine.Length; i++)
                {
                    char c = bottomLine[i];
                    bottomLineText.text += c;

                    if (!startedAudio && !char.IsWhiteSpace(c))
                    {
                        StartTypingAudio();
                        startedAudio = true;
                    }

                    bool isLastChar = i == bottomLine.Length - 1;

                    // On the *last* character, stop the typing sound immediately.
                    if (isLastChar)
                    {
                        if (_typingSource != null && _typingSource.isPlaying)
                        {
                            _typingSource.Stop();
                        }

                        // No extra per-character wait here; RunSplash will handle the 2s hold.
                    }
                    else
                    {
                        // For all earlier characters, keep the normal typing rhythm.
                        yield return new WaitForSeconds(wait);
                    }
                }
            }

            // No additional waits or audio stops here; at this point the last letter has just appeared,
            // and RunSplash() will now wait 2 seconds before changing scene.
        }

        private void StartTypingAudio()
        {
            if (_typingSource == null || _typingSource.isPlaying) return;
            _typingSource.Play();
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
