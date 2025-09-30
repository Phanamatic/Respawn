using UnityEngine;
using UnityEngine.EventSystems;
using DG.Tweening;
using TMPro;

namespace UI.Scripts
{
    public class ButtonPressEffect : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [Header("Press Effect Settings")]
        [SerializeField] private float squeezeAmount = 0.9f; // 0.9 = 10% squeeze from both sides
        [SerializeField] private float squeezeDuration = 0.1f;
        [SerializeField] private float growAmount = 1.1f; // 1.1 = 10% growth
        [SerializeField] private float growDuration = 0.2f;

        private TextMeshProUGUI buttonText;
        private Vector3 originalScale;
        private Sequence currentSequence;

        private void Start()
        {
            buttonText = GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                originalScale = buttonText.transform.localScale;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (buttonText == null) return;

            KillCurrentAnimation();

            // Squeeze effect (horizontal squeeze)
            Vector3 squeezeScale = new Vector3(originalScale.x * squeezeAmount, originalScale.y, originalScale.z);
            buttonText.transform.DOScale(squeezeScale, squeezeDuration).SetEase(Ease.OutQuad);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (buttonText == null) return;

            KillCurrentAnimation();

            // Grow then return to normal
            currentSequence = DOTween.Sequence();
            Vector3 growScale = originalScale * growAmount;

            currentSequence.Append(buttonText.transform.DOScale(growScale, growDuration).SetEase(Ease.OutBack));
            currentSequence.Append(buttonText.transform.DOScale(originalScale, growDuration).SetEase(Ease.InOutQuad));
        }

        private void KillCurrentAnimation()
        {
            if (currentSequence != null && currentSequence.IsActive())
            {
                currentSequence.Kill();
            }
            buttonText.transform.DOKill();
        }

        private void OnDestroy()
        {
            KillCurrentAnimation();
        }
    }
}