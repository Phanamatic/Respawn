using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;

namespace UI.Scripts
{
    public class NewsCarousel : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Carousel Settings")]
        [SerializeField] private List<Image> newsImages = new List<Image>();
        [SerializeField] private RectTransform imagesContainer;
        [SerializeField] private float autoScrollDuration = 4f;
        [SerializeField] private float transitionSpeed = 0.5f;

        [Header("Navigation Dots")]
        [SerializeField] private List<Image> navigationDots = new List<Image>();
        [SerializeField] private Color activeDotColor = Color.white;
        [SerializeField] private Color inactiveDotColor = new Color(1f, 1f, 1f, 0.3f);

        [Header("Visual States")]
        [SerializeField] private float activeAlpha = 1f;
        [SerializeField] private float inactiveAlpha = 0.3f;
        [SerializeField] private bool fadeTextChildren = true;

        [Header("Manual Control")]
        [SerializeField] private float mouseWheelSensitivity = 1f;
        [SerializeField] private float manualScrollCooldown = 0.5f;

        private int currentActiveIndex = 0; // Which image is currently active (in position 1)
        private float autoScrollTimer = 0f;
        private bool isTransitioning = false;
        private bool isHovering = false;
        private float manualScrollTimer = 0f;

        // Positions within the container (all constrained to container bounds)
        private Vector2 activePosition = Vector2.zero;      // Center position (visible)
        private Vector2 inactivePosition1 = Vector2.zero;   // Hidden position 1
        private Vector2 inactivePosition2 = Vector2.zero;   // Hidden position 2

        // Cache text components for each image
        private List<Text[]> imageLegacyTexts = new List<Text[]>();
        private List<TMPro.TextMeshProUGUI[]> imageTMPTexts = new List<TMPro.TextMeshProUGUI[]>();

        private void Awake()
        {
            if (newsImages.Count != 3)
            {
                Debug.LogWarning("NewsCarousel: Exactly 3 images required!");
                return;
            }

            // Auto-assign container if not set
            if (imagesContainer == null && newsImages[0] != null)
            {
                imagesContainer = newsImages[0].transform.parent.GetComponent<RectTransform>();
            }

            SetupPositions();
            SetupImages();
            CacheTextComponents();
            SetupNavigationDots();
        }

        private void SetupPositions()
        {
            if (imagesContainer == null) return;

            // All positions are within the container bounds
            activePosition = Vector2.zero; // Center of container (visible)

            // Hidden positions - you can adjust these values to position inactive images
            // They should be within container bounds but not visible (behind active image or off to sides slightly)
            inactivePosition1 = new Vector2(-50f, 0f);  // Slightly left
            inactivePosition2 = new Vector2(50f, 0f);   // Slightly right
        }

        private void Start()
        {
            PositionAllImages(false);
            autoScrollTimer = autoScrollDuration;
        }

        private void Update()
        {
            HandleAutoScroll();
            HandleManualScroll();
        }

        private void HandleAutoScroll()
        {
            if (isTransitioning) return;

            autoScrollTimer -= Time.deltaTime;
            if (autoScrollTimer <= 0f)
            {
                NextImage();
                autoScrollTimer = autoScrollDuration;
            }
        }

        private void HandleManualScroll()
        {
            if (!isHovering || isTransitioning) return;

            if (manualScrollTimer > 0f)
            {
                manualScrollTimer -= Time.deltaTime;
                return;
            }

            float scroll = Input.mouseScrollDelta.y * mouseWheelSensitivity;
            if (Mathf.Abs(scroll) > 0.1f)
            {
                if (scroll > 0)
                    PreviousImage();
                else
                    NextImage();

                manualScrollTimer = manualScrollCooldown;
                autoScrollTimer = autoScrollDuration; // Reset auto scroll timer
            }
        }

        private void SetupImages()
        {
            if (imagesContainer == null) return;

            for (int i = 0; i < newsImages.Count; i++)
            {
                if (newsImages[i] != null)
                {
                    RectTransform imageRect = newsImages[i].rectTransform;

                    // Set anchors to center-center for precise positioning
                    imageRect.anchorMin = new Vector2(0.5f, 0.5f);
                    imageRect.anchorMax = new Vector2(0.5f, 0.5f);
                    imageRect.sizeDelta = imagesContainer.rect.size; // Same size as container
                }
            }

            Debug.Log($"NewsCarousel: Setup {newsImages.Count} images");
        }

        private void CacheTextComponents()
        {
            imageLegacyTexts.Clear();
            imageTMPTexts.Clear();

            for (int i = 0; i < newsImages.Count; i++)
            {
                if (newsImages[i] != null)
                {
                    // Cache Legacy Text components
                    Text[] legacyTexts = newsImages[i].GetComponentsInChildren<Text>();
                    imageLegacyTexts.Add(legacyTexts);

                    // Cache TextMeshPro components
                    TMPro.TextMeshProUGUI[] tmpTexts = newsImages[i].GetComponentsInChildren<TMPro.TextMeshProUGUI>();
                    imageTMPTexts.Add(tmpTexts);

                    Debug.Log($"NewsCarousel: Image {i} has {legacyTexts.Length} Legacy texts and {tmpTexts.Length} TMP texts");
                }
                else
                {
                    imageLegacyTexts.Add(new Text[0]);
                    imageTMPTexts.Add(new TMPro.TextMeshProUGUI[0]);
                }
            }
        }

        private void SetupNavigationDots()
        {
            if (navigationDots.Count != newsImages.Count)
            {
                Debug.LogWarning("NewsCarousel: Navigation dots count doesn't match images count!");
                return;
            }

            for (int i = 0; i < navigationDots.Count; i++)
            {
                if (navigationDots[i] != null)
                {
                    navigationDots[i].color = inactiveDotColor;
                }
            }
        }

        public void NextImage()
        {
            if (isTransitioning) return;
            currentActiveIndex = (currentActiveIndex + 1) % 3;
            PositionAllImages(true);
        }

        public void PreviousImage()
        {
            if (isTransitioning) return;
            currentActiveIndex = (currentActiveIndex - 1 + 3) % 3;
            PositionAllImages(true);
        }

        private void PositionAllImages(bool animated = true)
        {
            UpdateNavigationDots();

            // Calculate which image goes where based on current active index
            int[] imageOrder = GetImageOrder();

            if (animated)
            {
                isTransitioning = true;
                int completedTweens = 0;

                for (int i = 0; i < 3; i++)
                {
                    int imageIndex = imageOrder[i];
                    Vector2 targetPosition = GetPositionForSlot(i);
                    float targetAlpha = (i == 0) ? activeAlpha : inactiveAlpha; // Slot 0 is active

                    // Position tween
                    DOTween.To(() => newsImages[imageIndex].rectTransform.anchoredPosition,
                              x => newsImages[imageIndex].rectTransform.anchoredPosition = x,
                              targetPosition, transitionSpeed)
                        .SetEase(Ease.OutQuad)
                        .OnComplete(() =>
                        {
                            completedTweens++;
                            if (completedTweens >= 3)
                                isTransitioning = false;
                        });

                    // Alpha tween for image
                    DOTween.To(() => newsImages[imageIndex].color.a,
                              x => {
                                  Color color = newsImages[imageIndex].color;
                                  color.a = x;
                                  newsImages[imageIndex].color = color;
                              },
                              targetAlpha, transitionSpeed);

                    // Alpha tween for text children
                    if (fadeTextChildren)
                    {
                        AnimateTextAlpha(imageIndex, targetAlpha, transitionSpeed);
                    }
                }
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    int imageIndex = imageOrder[i];
                    Vector2 targetPosition = GetPositionForSlot(i);
                    float targetAlpha = (i == 0) ? activeAlpha : inactiveAlpha;

                    newsImages[imageIndex].rectTransform.anchoredPosition = targetPosition;

                    Color color = newsImages[imageIndex].color;
                    color.a = targetAlpha;
                    newsImages[imageIndex].color = color;

                    // Set text alpha immediately
                    if (fadeTextChildren)
                    {
                        SetTextAlpha(imageIndex, targetAlpha);
                    }
                }
            }

            Debug.Log($"NewsCarousel: Active image is now {currentActiveIndex}");
        }

        private int[] GetImageOrder()
        {
            // Returns array of image indices in order: [active, inactive1, inactive2]
            int[] order = new int[3];
            order[0] = currentActiveIndex; // Active position
            order[1] = (currentActiveIndex + 1) % 3; // Next image (inactive)
            order[2] = (currentActiveIndex + 2) % 3; // Last image (inactive)
            return order;
        }

        private Vector2 GetPositionForSlot(int slot)
        {
            switch (slot)
            {
                case 0: return activePosition;      // Active/visible
                case 1: return inactivePosition1;   // Hidden position 1
                case 2: return inactivePosition2;   // Hidden position 2
                default: return activePosition;
            }
        }

        private void AnimateTextAlpha(int imageIndex, float targetAlpha, float duration)
        {
            if (imageIndex < 0 || imageIndex >= newsImages.Count) return;

            // Animate Legacy Text components
            if (imageIndex < imageLegacyTexts.Count)
            {
                foreach (Text text in imageLegacyTexts[imageIndex])
                {
                    if (text != null)
                    {
                        DOTween.To(() => text.color.a,
                                  x => {
                                      Color color = text.color;
                                      color.a = x;
                                      text.color = color;
                                  },
                                  targetAlpha, duration);
                    }
                }
            }

            // Animate TextMeshPro components
            if (imageIndex < imageTMPTexts.Count)
            {
                foreach (TMPro.TextMeshProUGUI tmpText in imageTMPTexts[imageIndex])
                {
                    if (tmpText != null)
                    {
                        DOTween.To(() => tmpText.color.a,
                                  x => {
                                      Color color = tmpText.color;
                                      color.a = x;
                                      tmpText.color = color;
                                  },
                                  targetAlpha, duration);
                    }
                }
            }
        }

        private void SetTextAlpha(int imageIndex, float alpha)
        {
            if (imageIndex < 0 || imageIndex >= newsImages.Count) return;

            // Set Legacy Text alpha immediately
            if (imageIndex < imageLegacyTexts.Count)
            {
                foreach (Text text in imageLegacyTexts[imageIndex])
                {
                    if (text != null)
                    {
                        Color color = text.color;
                        color.a = alpha;
                        text.color = color;
                    }
                }
            }

            // Set TextMeshPro alpha immediately
            if (imageIndex < imageTMPTexts.Count)
            {
                foreach (TMPro.TextMeshProUGUI tmpText in imageTMPTexts[imageIndex])
                {
                    if (tmpText != null)
                    {
                        Color color = tmpText.color;
                        color.a = alpha;
                        tmpText.color = color;
                    }
                }
            }
        }

        private void UpdateNavigationDots()
        {
            for (int i = 0; i < navigationDots.Count; i++)
            {
                if (navigationDots[i] != null)
                {
                    navigationDots[i].color = (i == currentActiveIndex) ? activeDotColor : inactiveDotColor;
                }
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovering = true;
            // Change cursor to pointer
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isHovering = false;
            manualScrollTimer = 0f;
            // Reset cursor
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        private void OnDisable()
        {
            if (imagesContainer != null && imagesContainer.gameObject != null)
                imagesContainer.DOKill();
        }

        private void OnDestroy()
        {
            if (imagesContainer != null && imagesContainer.gameObject != null)
                imagesContainer.DOKill();
        }

        // Public methods for external control
        public void SetAutoScrollDuration(float duration)
        {
            autoScrollDuration = duration;
        }

        public void SetTransitionSpeed(float speed)
        {
            transitionSpeed = speed;
        }

        public int GetCurrentImageIndex()
        {
            return currentActiveIndex;
        }

        public int GetImageCount()
        {
            return newsImages.Count;
        }
    }
}