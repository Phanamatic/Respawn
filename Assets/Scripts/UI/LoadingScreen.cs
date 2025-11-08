using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace UI.Scripts
{
    public class LoadingScreen : MonoBehaviour
    {
        [Header("Scene")]
        [SerializeField] private string nextSceneName = "MainMenu";

        [Header("Loading Time")]
// Removed fake min/max load timing (now using real AsyncOperation.progress).
// Brief dev comment: eliminates CS0414 warnings.

        [Header("UI References")]
        [SerializeField] private Image fillImage;
        [SerializeField] private TextMeshProUGUI tipsText;

        [Header("Fill Settings")]
// Removed: staged fill count no longer used with real AsyncOperation.progress.
// Dev: silences CS0414 on numberOfFillStages.
        [SerializeField] private float rotationSpeed = 180f;
        [SerializeField] private float pauseAt95Duration = 0.2f; // Pause time at 95%
        [SerializeField] private float finalSmoothDuration = 0.5f; // Smooth transition time for 95% to 100%

        [Header("Tips")]
        [SerializeField] private float tipChangeInterval = 3f;
        [SerializeField, TextArea(2, 5)] private string[] tips = new string[]
        {
            "Stay on the move to avoid enemy fire!",
            "Use cover wisely - it can save your life.",
            "Team coordination is key to victory.",
            "Each weapon has unique strengths - experiment to find your favorite.",
            "Watch your ammo count and reload when safe.",
            "Communication with teammates increases your chances of winning.",
            "Learn the map layouts to gain a tactical advantage.",
            "Don't forget to check your corners!",
            "Practice makes perfect - keep playing to improve your skills.",
            "Timing your abilities can turn the tide of battle."
        };

// Removed legacy timing fields.
// Brief dev comment: these supported the fake staged loader.
        private List<string> shuffledTips;
        private int currentTipIndex = 0;
        private AsyncOperation _loadOp; // track the real async scene load so UI reflects actual progress
// Brief dev comment: old staged list no longer used.
// Drive progress from SceneManager.LoadSceneAsync instead of fake stages.

        private void Start()
        {
            // Initialize and shuffle tips
            ShuffleTips();

            // Start coroutines
            StartCoroutine(FillProgressionCoroutine()); // now tied to real async load
            StartCoroutine(TipsRotationCoroutine());

            // Show first tip immediately
            if (tipsText != null && shuffledTips.Count > 0)
            {
                tipsText.text = shuffledTips[currentTipIndex];
            }

            // Set initial fill to 0
            if (fillImage != null)
            {
                fillImage.fillAmount = 0f;
            }
        }
// Remove fake staged timing; we’ll reflect actual load progress.

        private void Update()
        {
            // Rotate fill image continuously on Y-axis
            if (fillImage != null)
            {
                fillImage.transform.Rotate(0f, rotationSpeed * Time.deltaTime, 0f);
            }
        }

// Removed: staged fake progression helper not used anymore.
// Brief dev comment: progress now driven by _loadOp.progress.

        private void ShuffleTips()
        {
            shuffledTips = new List<string>(tips);

            // Fisher-Yates shuffle algorithm
            for (int i = shuffledTips.Count - 1; i > 0; i--)
            {
                int randomIndex = Random.Range(0, i + 1);
                string temp = shuffledTips[i];
                shuffledTips[i] = shuffledTips[randomIndex];
                shuffledTips[randomIndex] = temp;
            }
        }

        private IEnumerator FillProgressionCoroutine()
        {
            if (string.IsNullOrEmpty(nextSceneName))
            {
                Debug.LogError("[LoadingScreen] Next scene name is not set!");
                yield break;
            }

            // Begin the real async load and gate activation until UI reaches 100%
            _loadOp = SceneManager.LoadSceneAsync(nextSceneName);
            _loadOp.allowSceneActivation = false;

            // Update fill based on actual async progress (0..0.9 while loading)
            while (_loadOp != null && _loadOp.progress < 0.9f)
            {
                if (fillImage != null)
                {
                    // Normalize 0..0.9 to 0..1 for the ring
                    float target = Mathf.Clamp01(_loadOp.progress / 0.9f);
                    fillImage.fillAmount = target;
                }
                yield return null;
            }

            // Brief hold for anticipation then smooth the final stretch to 100%
            if (pauseAt95Duration > 0f)
                yield return new WaitForSeconds(pauseAt95Duration);

            yield return StartCoroutine(SmoothFinalFill());

            // Activate the scene once the bar hits 100%
            LoadNextScene();
        }
// Uses AsyncOperation.progress so the ring/tip reflect actual load state.

        private IEnumerator SmoothFinalFill()
        {
            if (fillImage == null) yield break;

            float startFill = fillImage.fillAmount;
            float elapsed = 0f;

            while (elapsed < finalSmoothDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / finalSmoothDuration;
                fillImage.fillAmount = Mathf.Lerp(startFill, 1f, t);
                yield return null;
            }

            // Ensure we reach exactly 100%
            fillImage.fillAmount = 1f;
        }

        private IEnumerator TipsRotationCoroutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(tipChangeInterval);

                // Move to next tip
                currentTipIndex++;

                // If we've shown all tips, reshuffle and start over
                if (currentTipIndex >= shuffledTips.Count)
                {
                    ShuffleTips();
                    currentTipIndex = 0;
                }

                // Display the new tip
                if (tipsText != null)
                {
                    tipsText.text = shuffledTips[currentTipIndex];
                }
            }
        }

        private void LoadNextScene()
        {
            if (_loadOp != null)
            {
                _loadOp.allowSceneActivation = true; // hand-off to the already loaded async op
                return;
            }

            if (string.IsNullOrEmpty(nextSceneName))
            {
                Debug.LogError("[LoadingScreen] Next scene name is not set!");
                return;
            }

            // Fallback (shouldn't normally hit if _loadOp drove progress)
            SceneManager.LoadSceneAsync(nextSceneName);
        }
// Activates the in-flight AsyncOperation instead of starting a second load.
    }
}
