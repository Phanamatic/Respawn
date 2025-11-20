using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Simple scene fade-in: given a full-screen panel with a CanvasGroup,
    /// starts at alpha 1 and fades down to 0 on scene load.
    /// </summary>
    public class SceneFadeIn : MonoBehaviour
    {
        [Header("Fade In")]
        [SerializeField] private CanvasGroup fadeCanvasGroup;
        [SerializeField] private float fadeInDuration = 1.0f;
        [SerializeField] private bool disablePanelOnComplete = true;

        private void Awake()
        {
            if (fadeCanvasGroup != null)
            {
                // Ensure we start fully black.
                fadeCanvasGroup.gameObject.SetActive(true);
                fadeCanvasGroup.alpha = 1f;
            }
        }

        private void Start()
        {
            if (fadeCanvasGroup != null && fadeInDuration > 0f)
            {
                StartCoroutine(FadeInCoroutine());
            }
            else if (fadeCanvasGroup != null)
            {
                // No duration: snap to transparent.
                fadeCanvasGroup.alpha = 0f;
                if (disablePanelOnComplete)
                    fadeCanvasGroup.gameObject.SetActive(false);
            }
        }

        private System.Collections.IEnumerator FadeInCoroutine()
        {
            float duration = Mathf.Max(0.01f, fadeInDuration);
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                fadeCanvasGroup.alpha = Mathf.Lerp(1f, 0f, t);
                yield return null;
            }

            fadeCanvasGroup.alpha = 0f;

            if (disablePanelOnComplete)
            {
                fadeCanvasGroup.gameObject.SetActive(false);
            }
        }
    }
}
// Fades Account scene in from black; plug into a full-screen panel CanvasGroup.