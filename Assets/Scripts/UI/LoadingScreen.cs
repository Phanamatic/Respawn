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
        [SerializeField] private float minLoadTime = 2f;
        [SerializeField] private float maxLoadTime = 5f;

        [Header("UI References")]
        [SerializeField] private Image fillImage;
        [SerializeField] private TextMeshProUGUI tipsText;

        [Header("Fill Settings")]
        [SerializeField] private int numberOfFillStages = 7; // How many jumps before 100%
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

        private float totalLoadTime;
        private float loadStartTime;
        private List<string> shuffledTips;
        private int currentTipIndex = 0;
        private List<float> fillStages;

        private void Start()
        {
            // Pick random load duration
            totalLoadTime = Random.Range(minLoadTime, maxLoadTime);
            loadStartTime = Time.time;

            // Generate randomized fill stages
            GenerateRandomFillStages();

            // Initialize and shuffle tips
            ShuffleTips();

            // Start coroutines
            StartCoroutine(FillProgressionCoroutine());
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

        private void Update()
        {
            // Rotate fill image continuously on Y-axis
            if (fillImage != null)
            {
                fillImage.transform.Rotate(0f, rotationSpeed * Time.deltaTime, 0f);
            }
        }

        private void GenerateRandomFillStages()
        {
            fillStages = new List<float>();
            fillStages.Add(0f); // Always start at 0%

            // Generate random stages between 0.1 and 0.95 (stop before final smooth section)
            for (int i = 0; i < numberOfFillStages; i++)
            {
                float randomStage = Random.Range(0.1f, 0.95f);
                fillStages.Add(randomStage);
            }

            // Sort the stages so they're in ascending order
            fillStages.Sort();

            // Remove any stages above 0.95 since we'll smooth from 95% to 100%
            fillStages.RemoveAll(stage => stage > 0.95f);

            // Add the final stage at 0.95 (will smooth to 100%)
            if (fillStages[fillStages.Count - 1] < 0.95f)
            {
                fillStages.Add(0.95f);
            }
        }

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
            for (int i = 0; i < fillStages.Count; i++)
            {
                float targetStage = fillStages[i];
                float targetTime = loadStartTime + (totalLoadTime * targetStage);

                // Wait until we reach the time for this stage
                yield return new WaitUntil(() => Time.time >= targetTime);

                // Jump to this fill stage
                if (fillImage != null)
                {
                    fillImage.fillAmount = targetStage;
                }

                // If we've reached 95%, pause then start smooth transition to 100%
                if (targetStage >= 0.95f)
                {
                    yield return new WaitForSeconds(pauseAt95Duration); // Pause at 95%
                    yield return StartCoroutine(SmoothFinalFill());
                    yield return new WaitForSeconds(0.1f); // Small delay to show 100%
                    LoadNextScene();
                    yield break;
                }
            }
        }

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
            if (string.IsNullOrEmpty(nextSceneName))
            {
                Debug.LogError("[LoadingScreen] Next scene name is not set!");
                return;
            }

            // Use async for smoother transition
            SceneManager.LoadSceneAsync(nextSceneName);
        }
    }
}
