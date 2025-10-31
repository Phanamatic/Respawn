using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace UI.Scripts
{
    public enum PanelSlideDirection
    {
        Left,
        Right,
        Top,
        Bottom
    }

    public class SlidingPanel : MonoBehaviour
    {
        [Header("Panel Settings")]
        [SerializeField] private RectTransform panelRectTransform;
        [SerializeField] private Button closeButton;

        [Header("Linked Panel")]
        [SerializeField] private RectTransform linkedPanel;

        [Header("Animation Settings")]
        [SerializeField] private float scanInDuration = 0.3f;
        [SerializeField] private float scanOutDuration = 0.3f;
        [SerializeField] private Ease scanInEase = Ease.OutCubic;
        [SerializeField] private Ease scanOutEase = Ease.InCubic;

        [Header("Slide Direction")]
        [SerializeField] private PanelSlideDirection slideInDirection = PanelSlideDirection.Left;
        [SerializeField] private PanelSlideDirection slideOutDirection = PanelSlideDirection.Right;

        [Header("Click Outside Behavior")]
        [SerializeField] private bool closeOnClickOutside = true;

        private Vector2 hiddenInPosition;
        private Vector2 hiddenOutPosition;
        private Vector2 visiblePosition;
        private Vector2 linkedHiddenInPosition;
        private Vector2 linkedHiddenOutPosition;
        private Vector2 linkedVisiblePosition;
        private bool isOpen = false;
        private Sequence currentSequence;

        private void Awake()
        {
            if (panelRectTransform == null)
            {
                panelRectTransform = GetComponent<RectTransform>();
            }

            // Calculate positions for main panel based on slide directions
            visiblePosition = panelRectTransform.anchoredPosition;
            hiddenInPosition = CalculateHiddenPosition(visiblePosition, panelRectTransform.rect, slideInDirection);
            hiddenOutPosition = CalculateHiddenPosition(visiblePosition, panelRectTransform.rect, slideOutDirection);

            // Calculate positions for linked panel
            if (linkedPanel != null)
            {
                linkedVisiblePosition = linkedPanel.anchoredPosition;
                linkedHiddenInPosition = CalculateHiddenPosition(linkedVisiblePosition, linkedPanel.rect, slideInDirection);
                linkedHiddenOutPosition = CalculateHiddenPosition(linkedVisiblePosition, linkedPanel.rect, slideOutDirection);
                linkedPanel.anchoredPosition = linkedHiddenInPosition;
            }

            // Start hidden
            panelRectTransform.anchoredPosition = hiddenInPosition;
            gameObject.SetActive(false);

            // Setup close button
            if (closeButton != null)
            {
                closeButton.onClick.AddListener(ClosePanel);
            }
        }

        private Vector2 CalculateHiddenPosition(Vector2 visiblePos, Rect rect, PanelSlideDirection direction)
        {
            switch (direction)
            {
                case PanelSlideDirection.Left:
                    return new Vector2(-Screen.width - rect.width, visiblePos.y);
                case PanelSlideDirection.Right:
                    return new Vector2(Screen.width + rect.width, visiblePos.y);
                case PanelSlideDirection.Top:
                    return new Vector2(visiblePos.x, Screen.height + rect.height);
                case PanelSlideDirection.Bottom:
                    return new Vector2(visiblePos.x, -Screen.height - rect.height);
                default:
                    return visiblePos;
            }
        }

        private void Update()
        {
            if (closeOnClickOutside && isOpen && Input.GetMouseButtonDown(0))
            {
                CheckClickOutside();
            }
        }

        private void CheckClickOutside()
        {
            // Use RectTransform bounds to check if click is outside panel
            Vector2 mousePosition = Input.mousePosition;
            if (!RectTransformUtility.RectangleContainsScreenPoint(panelRectTransform, mousePosition, null))
            {
                ClosePanel();
            }
        }

        public void OpenPanel()
        {
            if (isOpen) return;

            gameObject.SetActive(true);
            isOpen = true;

            // Slide in based on configured direction
            KillCurrentAnimation();
            panelRectTransform.anchoredPosition = hiddenInPosition;
            panelRectTransform.DOAnchorPos(visiblePosition, scanInDuration).SetEase(scanInEase);

            // Animate linked panel sliding in
            if (linkedPanel != null)
            {
                linkedPanel.anchoredPosition = linkedHiddenInPosition;
                linkedPanel.DOAnchorPos(linkedVisiblePosition, scanInDuration).SetEase(scanInEase);
            }
        }

        public void ClosePanel()
        {
            if (!isOpen) return;

            isOpen = false;

            // Slide out based on configured direction
            KillCurrentAnimation();
            currentSequence = DOTween.Sequence();
            currentSequence.Append(panelRectTransform.DOAnchorPos(hiddenOutPosition, scanOutDuration).SetEase(scanOutEase));
            currentSequence.OnComplete(() => gameObject.SetActive(false));

            // Animate linked panel sliding out
            if (linkedPanel != null)
            {
                linkedPanel.DOAnchorPos(linkedHiddenOutPosition, scanOutDuration).SetEase(scanOutEase);
            }
        }

        private void KillCurrentAnimation()
        {
            if (currentSequence != null && currentSequence.IsActive())
            {
                currentSequence.Kill();
            }
            panelRectTransform.DOKill();

            if (linkedPanel != null)
            {
                linkedPanel.DOKill();
            }
        }

        public bool IsOpen()
        {
            return isOpen;
        }

        private void OnDestroy()
        {
            if (closeButton != null)
            {
                closeButton.onClick.RemoveListener(ClosePanel);
            }
            KillCurrentAnimation();
        }
    }
}