using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using System.Collections.Generic;

namespace UI.Scripts
{
    [RequireComponent(typeof(Button))]
    public class AccountInfoClick : MonoBehaviour, IPointerClickHandler
    {
        [Header("Preview Panel")]
        [SerializeField] private RectTransform previewPanel;
        [SerializeField] private CanvasGroup panelCanvasGroup;

        [Header("Animation Settings")]
        [SerializeField] private float slideDistance = 200f;
        [SerializeField] private float animationDuration = 0.3f;
        [SerializeField] private Ease slideEase = Ease.OutQuad;

        [Header("Text Fade Settings")]
        [SerializeField] private bool fadeTextWithPanel = true;
        [SerializeField] private float textFadeDelay = 0.1f; // Slight delay for text fade

        [Header("Panel Positioning")]
        [SerializeField] private SlideDirection slideDirection = SlideDirection.Down;
        [SerializeField] private bool useCanvasGroupForFade = true;

        [Header("Click Outside Detection")]
        [SerializeField] private bool closeOnClickOutside = true;

        private Button button;
        private Vector3 originalPanelPosition;
        private Vector3 hiddenPanelPosition;
        private bool isPanelVisible = false;
        private bool isAnimating = false;

        // Cached text components
        private List<Text> legacyTexts = new List<Text>();
        private List<TextMeshProUGUI> tmpTexts = new List<TextMeshProUGUI>();

        // DOTween sequences
        private Sequence currentSequence;

        // Static reference to track currently open panel
        private static AccountInfoClick currentlyOpenPanel;

        public enum SlideDirection
        {
            Up,
            Down,
            Left,
            Right
        }

        private void Awake()
        {
            button = GetComponent<Button>();

            if (previewPanel != null)
            {
                originalPanelPosition = previewPanel.localPosition;
                CalculateHiddenPosition();
                CacheTextComponents();
                SetupPanel();
            }
            else
            {
                Debug.LogWarning("AccountInfoClick: Preview panel not assigned!");
            }
        }

        private void Start()
        {
            // Add click listener to button
            if (button != null)
                button.onClick.AddListener(OnButtonClicked);
        }

        private void Update()
        {
            // Check for clicks outside the panel
            if (isPanelVisible && closeOnClickOutside && Input.GetMouseButtonDown(0))
            {
                CheckClickOutside();
            }
        }

        private void CheckClickOutside()
        {
            // Get mouse position in screen space
            Vector2 mousePosition = Input.mousePosition;

            // Check if click is outside both the button and the panel
            if (!RectTransformUtility.RectangleContainsScreenPoint(GetComponent<RectTransform>(), mousePosition, null) &&
                !RectTransformUtility.RectangleContainsScreenPoint(previewPanel, mousePosition, null))
            {
                HidePanel();
            }
        }

        private void CalculateHiddenPosition()
        {
            Vector3 offset = Vector3.zero;

            switch (slideDirection)
            {
                case SlideDirection.Up:
                    offset = Vector3.up * slideDistance;
                    break;
                case SlideDirection.Down:
                    offset = Vector3.down * slideDistance;
                    break;
                case SlideDirection.Left:
                    offset = Vector3.left * slideDistance;
                    break;
                case SlideDirection.Right:
                    offset = Vector3.right * slideDistance;
                    break;
            }

            hiddenPanelPosition = originalPanelPosition + offset;
        }

        private void CacheTextComponents()
        {
            if (previewPanel == null) return;

            legacyTexts.Clear();
            tmpTexts.Clear();

            // Find all text components in the panel
            legacyTexts.AddRange(previewPanel.GetComponentsInChildren<Text>());
            tmpTexts.AddRange(previewPanel.GetComponentsInChildren<TextMeshProUGUI>());

            Debug.Log($"AccountInfoClick: Found {legacyTexts.Count} Legacy texts and {tmpTexts.Count} TMP texts");
        }

        private void SetupPanel()
        {
            if (previewPanel == null) return;

            // Start with panel hidden
            previewPanel.localPosition = hiddenPanelPosition;

            // Setup canvas group if using it for fade
            if (useCanvasGroupForFade)
            {
                if (panelCanvasGroup == null)
                    panelCanvasGroup = previewPanel.GetComponent<CanvasGroup>();

                if (panelCanvasGroup == null)
                    panelCanvasGroup = previewPanel.gameObject.AddComponent<CanvasGroup>();

                panelCanvasGroup.alpha = 0f;
            }
            else
            {
                // Set individual text alpha to 0
                SetTextAlpha(0f);
            }

            // Ensure panel is active but invisible
            previewPanel.gameObject.SetActive(true);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // This handles clicks on the button itself
            OnButtonClicked();
        }

        private void OnButtonClicked()
        {
            if (isAnimating) return;

            if (isPanelVisible)
            {
                HidePanel();
            }
            else
            {
                // Close any other open panels first
                if (currentlyOpenPanel != null && currentlyOpenPanel != this)
                {
                    currentlyOpenPanel.HidePanel();
                }

                ShowPanel();
            }
        }

        private void ShowPanel()
        {
            if (previewPanel == null || isAnimating) return;

            KillCurrentAnimation();
            isAnimating = true;
            isPanelVisible = true;
            currentlyOpenPanel = this;

            currentSequence = DOTween.Sequence();

            if (useCanvasGroupForFade)
            {
                // Animate panel position and canvas group alpha together
                currentSequence.Append(previewPanel.DOLocalMove(originalPanelPosition, animationDuration).SetEase(slideEase));
                currentSequence.Join(panelCanvasGroup.DOFade(1f, animationDuration));
            }
            else
            {
                // Animate panel position
                currentSequence.Append(previewPanel.DOLocalMove(originalPanelPosition, animationDuration).SetEase(slideEase));

                // Animate text fade with slight delay
                if (fadeTextWithPanel)
                {
                    currentSequence.Append(DOTween.To(() => 0f, x => SetTextAlpha(x), 1f, animationDuration - textFadeDelay)
                        .SetDelay(textFadeDelay));
                }
            }

            currentSequence.OnComplete(() => isAnimating = false);

            Debug.Log("AccountInfoClick: Showing panel");
        }

        private void HidePanel()
        {
            if (previewPanel == null || isAnimating || !isPanelVisible) return;

            KillCurrentAnimation();
            isAnimating = true;
            isPanelVisible = false;

            if (currentlyOpenPanel == this)
                currentlyOpenPanel = null;

            currentSequence = DOTween.Sequence();

            if (useCanvasGroupForFade)
            {
                // Animate panel position and canvas group alpha together
                currentSequence.Append(previewPanel.DOLocalMove(hiddenPanelPosition, animationDuration).SetEase(slideEase));
                currentSequence.Join(panelCanvasGroup.DOFade(0f, animationDuration));
            }
            else
            {
                // Animate text fade first
                if (fadeTextWithPanel)
                {
                    currentSequence.Append(DOTween.To(() => 1f, x => SetTextAlpha(x), 0f, animationDuration - textFadeDelay));
                }

                // Then animate panel position
                currentSequence.Append(previewPanel.DOLocalMove(hiddenPanelPosition, animationDuration - textFadeDelay).SetEase(slideEase));
            }

            currentSequence.OnComplete(() => isAnimating = false);

            Debug.Log("AccountInfoClick: Hiding panel");
        }

        private void SetTextAlpha(float alpha)
        {
            // Set Legacy Text alpha
            foreach (Text text in legacyTexts)
            {
                if (text != null)
                {
                    Color color = text.color;
                    color.a = alpha;
                    text.color = color;
                }
            }

            // Set TextMeshPro alpha
            foreach (TextMeshProUGUI tmpText in tmpTexts)
            {
                if (tmpText != null)
                {
                    Color color = tmpText.color;
                    color.a = alpha;
                    tmpText.color = color;
                }
            }
        }

        private void KillCurrentAnimation()
        {
            if (currentSequence != null && currentSequence.IsActive())
            {
                currentSequence.Kill();
                currentSequence = null;
            }
        }

        private void OnDisable()
        {
            KillCurrentAnimation();
            isPanelVisible = false;
            isAnimating = false;

            if (currentlyOpenPanel == this)
                currentlyOpenPanel = null;
        }

        private void OnDestroy()
        {
            KillCurrentAnimation();

            if (currentlyOpenPanel == this)
                currentlyOpenPanel = null;

            // Remove button listener
            if (button != null)
                button.onClick.RemoveListener(OnButtonClicked);
        }

        // Public methods for runtime configuration
        public void SetSlideDistance(float distance)
        {
            slideDistance = distance;
            CalculateHiddenPosition();
        }

        public void SetAnimationDuration(float duration)
        {
            animationDuration = duration;
        }

        public void SetSlideDirection(SlideDirection direction)
        {
            slideDirection = direction;
            CalculateHiddenPosition();
        }

        public void ForceHide()
        {
            if (previewPanel == null) return;

            KillCurrentAnimation();
            isPanelVisible = false;
            isAnimating = false;

            if (currentlyOpenPanel == this)
                currentlyOpenPanel = null;

            previewPanel.localPosition = hiddenPanelPosition;

            if (useCanvasGroupForFade && panelCanvasGroup != null)
                panelCanvasGroup.alpha = 0f;
            else
                SetTextAlpha(0f);
        }

        public bool IsPanelVisible()
        {
            return isPanelVisible;
        }
    }
}