using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace UI.Scripts
{
    public class SlidingPanel : MonoBehaviour
    {
        [Header("Panel Settings")]
        [SerializeField] private RectTransform panelRectTransform;
        [SerializeField] private Button closeButton;

        [Header("Linked Panel")]
        [SerializeField] private RectTransform linkedPanel;

        [Header("Animation Settings")]
        [SerializeField] private float slideDownDuration = 0.5f;
        [SerializeField] private float slideUpDuration = 0.4f;
        [SerializeField] private Ease slideDownEase = Ease.OutFlash;
        [SerializeField] private Ease slideUpEase = Ease.InQuad;

        private Vector2 hiddenPosition;
        private Vector2 visiblePosition;
        private Vector2 linkedHiddenPosition;
        private Vector2 linkedVisiblePosition;
        private bool isOpen = false;
        private Sequence currentSequence;

        private void Awake()
        {
            if (panelRectTransform == null)
            {
                panelRectTransform = GetComponent<RectTransform>();
            }

            // Calculate positions for main panel
            visiblePosition = panelRectTransform.anchoredPosition;
            hiddenPosition = new Vector2(visiblePosition.x, Screen.height + panelRectTransform.rect.height);

            // Calculate positions for linked panel
            if (linkedPanel != null)
            {
                linkedVisiblePosition = linkedPanel.anchoredPosition;
                linkedHiddenPosition = new Vector2(linkedVisiblePosition.x, Screen.height + linkedPanel.rect.height);
                linkedPanel.anchoredPosition = linkedHiddenPosition;
            }

            // Start hidden
            panelRectTransform.anchoredPosition = hiddenPosition;
            gameObject.SetActive(false);

            // Setup close button
            if (closeButton != null)
            {
                closeButton.onClick.AddListener(ClosePanel);
            }
        }

        private void Update()
        {
            if (isOpen && Input.GetMouseButtonDown(0))
            {
                CheckClickOutside();
            }
        }

        private void CheckClickOutside()
        {
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

            // Animate main panel sliding down
            KillCurrentAnimation();
            panelRectTransform.anchoredPosition = hiddenPosition;
            panelRectTransform.DOAnchorPos(visiblePosition, slideDownDuration).SetEase(slideDownEase);

            // Animate linked panel sliding down
            if (linkedPanel != null)
            {
                linkedPanel.anchoredPosition = linkedHiddenPosition;
                linkedPanel.DOAnchorPos(linkedVisiblePosition, slideDownDuration).SetEase(slideDownEase);
            }
        }

        public void ClosePanel()
        {
            if (!isOpen) return;

            isOpen = false;

            // Animate main panel sliding up
            KillCurrentAnimation();
            currentSequence = DOTween.Sequence();
            currentSequence.Append(panelRectTransform.DOAnchorPos(hiddenPosition, slideUpDuration).SetEase(slideUpEase));
            currentSequence.OnComplete(() => gameObject.SetActive(false));

            // Animate linked panel sliding up
            if (linkedPanel != null)
            {
                linkedPanel.DOAnchorPos(linkedHiddenPosition, slideUpDuration).SetEase(slideUpEase);
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