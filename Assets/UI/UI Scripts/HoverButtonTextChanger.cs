using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace UI.Scripts
{
    public enum SlideDirection
    {
        Left,
        Right,
        Up,
        Down
    }

    [RequireComponent(typeof(Button))]
    public class HoverButtonTextChanger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Text Settings")]
        [SerializeField] private TextMeshProUGUI targetText;

        [Header("Text Content")]
        [SerializeField] private string originalText = "Play";
        [SerializeField] private string hoverText = "Join Game";

        [Header("Colors")]
        [SerializeField] private Color originalColor = Color.white;
        [SerializeField] private Color hoverColor = Color.yellow;

        [Header("Animation")]
        [SerializeField] private float animationDuration = 0.3f;
        [SerializeField] private float slideDistance = 50f;
        [SerializeField] private SlideDirection slideDirection = SlideDirection.Right;

        private Button button;
        private Vector3 originalPosition;
        private bool isHovering = false;
        private Tween currentTween;

        private void Awake()
        {
            button = GetComponent<Button>();

            // Auto-find TextMeshPro component in children if not assigned
            if (targetText == null)
                targetText = GetComponentInChildren<TextMeshProUGUI>();

            if (targetText != null)
            {
                originalPosition = targetText.transform.localPosition;

                // Store original text and color from the component if not set in inspector
                if (string.IsNullOrEmpty(originalText))
                    originalText = targetText.text;

                if (originalColor == Color.white) // Default color check
                    originalColor = targetText.color;
            }
            else
            {
                Debug.LogWarning($"HoverButtonTextChanger on {gameObject.name}: No TextMeshProUGUI found in children!");
            }
        }

        private void Start()
        {
            if (targetText != null)
            {
                targetText.text = originalText;
                targetText.color = originalColor;
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!button.interactable || isHovering || targetText == null) return;

            isHovering = true;
            AnimateTextChange(hoverText, hoverColor, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!isHovering || targetText == null) return;

            isHovering = false;
            AnimateTextChange(originalText, originalColor, false);
        }

        private void AnimateTextChange(string newText, Color newColor, bool isEntering)
        {
            if (currentTween != null && currentTween.IsActive())
                currentTween.Kill();

            Vector3 slideDirection = GetSlideDirectionVector();
            Vector3 slideOutPosition = originalPosition + slideDirection * (isEntering ? slideDistance : -slideDistance);
            Vector3 slideInPosition = originalPosition + slideDirection * (isEntering ? -slideDistance : slideDistance);

            Sequence sequence = DOTween.Sequence();

            // Slide out current text
            if (slideDirection.x != 0)
                sequence.Append(targetText.transform.DOLocalMoveX(slideOutPosition.x, animationDuration / 2f).SetEase(Ease.OutQuad));
            else
                sequence.Append(targetText.transform.DOLocalMoveY(slideOutPosition.y, animationDuration / 2f).SetEase(Ease.OutQuad));

            // Change text and color at the midpoint
            sequence.AppendCallback(() =>
            {
                targetText.text = newText;
                targetText.color = newColor;
                targetText.transform.localPosition = slideInPosition;
            });

            // Slide in new text
            if (slideDirection.x != 0)
                sequence.Append(targetText.transform.DOLocalMoveX(originalPosition.x, animationDuration / 2f).SetEase(Ease.OutQuad));
            else
                sequence.Append(targetText.transform.DOLocalMoveY(originalPosition.y, animationDuration / 2f).SetEase(Ease.OutQuad));

            currentTween = sequence;
        }

        private Vector3 GetSlideDirectionVector()
        {
            switch (slideDirection)
            {
                case SlideDirection.Left:
                    return Vector3.left;
                case SlideDirection.Right:
                    return Vector3.right;
                case SlideDirection.Up:
                    return Vector3.up;
                case SlideDirection.Down:
                    return Vector3.down;
                default:
                    return Vector3.right;
            }
        }

        private void OnDisable()
        {
            if (currentTween != null && currentTween.IsActive())
                currentTween.Kill();

            isHovering = false;
        }

        private void OnDestroy()
        {
            if (currentTween != null && currentTween.IsActive())
                currentTween.Kill();
        }

        // Public methods for runtime configuration
        public void SetOriginalText(string text)
        {
            originalText = text;
            if (!isHovering && targetText != null)
                targetText.text = originalText;
        }

        public void SetHoverText(string text)
        {
            hoverText = text;
        }

        public void SetOriginalColor(Color color)
        {
            originalColor = color;
            if (!isHovering && targetText != null)
                targetText.color = originalColor;
        }

        public void SetHoverColor(Color color)
        {
            hoverColor = color;
        }
    }
}