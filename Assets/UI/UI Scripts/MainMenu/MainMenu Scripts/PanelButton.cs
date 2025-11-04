using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using DG.Tweening;
using System.Collections;

namespace UI.Scripts
{
    [RequireComponent(typeof(Button))]
    public class PanelButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Panel Reference")]
        [SerializeField] private SlidingPanel targetPanel;
        [SerializeField] private SlidingPanel oppositePanelToClose;

        [Header("Image References")]
        [SerializeField] private Image buttonImage;
        [SerializeField] private Image fillHoverImage; // Radial fill effect
        [SerializeField] private Image activeBackgroundImage;

        [Header("Opacity Settings")]
        [SerializeField] private float normalOpacity = 155f;
        [SerializeField] private float hoverOpacity = 255f;

        [Header("Radial Animation Settings")]
        [SerializeField] private float radialLoopDuration = 0.5f;
        [SerializeField] private float radialFinishSpeedMultiplier = 2f; // How much faster to finish when panel opens

        private Button button;
        private bool isHovering = false;
        private Tween radialTween;
        private Coroutine clickFlashCoroutine;
        private bool radialShouldFinish = false; // Flag to finish current cycle gracefully

        private void Awake()
        {
            button = GetComponent<Button>();

            // Auto-assign button image if not set
            if (buttonImage == null)
            {
                buttonImage = GetComponent<Image>();
            }

            // Set initial states
            SetImageOpacity(buttonImage, normalOpacity);

            if (fillHoverImage != null)
            {
                fillHoverImage.type = Image.Type.Filled;
                fillHoverImage.fillMethod = Image.FillMethod.Radial360;
                fillHoverImage.fillClockwise = true;
                fillHoverImage.fillAmount = 0f;
                SetImageOpacity(fillHoverImage, 0f);
            }

            if (activeBackgroundImage != null)
            {
                SetImageOpacity(activeBackgroundImage, 0f);
            }

            // Remove all existing listeners to prevent conflicts
            button.onClick.RemoveAllListeners();
        }

        private void Start()
        {
            // Add our open listener AFTER removing all others
            button.onClick.AddListener(OnButtonClicked);
        }

        private void Update()
        {
            // Update active background and radial based on panel state
            if (targetPanel != null)
            {
                bool panelIsOpen = targetPanel.IsOpen();

                // Make sure the button doesn't react to clicks when panel is open
                if (button != null)
                {
                    // Keep interactable true so visuals (hover/click states) stay the same
                    // but manually block clicks via EventSystem
                    if (panelIsOpen)
                    {
                        // Disable pointer clicks without disabling the button visuals
                        button.enabled = false;
                    }
                    else
                    {
                        button.enabled = true;
                    }
                }

                // Active background visibility
                if (activeBackgroundImage != null)
                {
                    SetImageOpacity(activeBackgroundImage, panelIsOpen ? 255f : 0f);
                }

                // Radial fill behavior based on panel state
                if (fillHoverImage != null)
                {
                    if (panelIsOpen)
                    {
                        // Panel is open (active): finish radial and keep at 100%
                        FinishRadialGracefully();
                        SetImageOpacity(fillHoverImage, 255f);
                    }
                    else if (!panelIsOpen && isHovering)
                    {
                        // Panel closed and hovering: loop radial continuously
                        radialShouldFinish = false; // Cancel any finish flag

                        if (radialTween == null || !radialTween.IsActive())
                        {
                            StartRadialLoop();
                        }
                        else if (radialTween.Loops() == 0)
                        {
                            // Was finishing, but user hovered again - restart loop
                            StartRadialLoop();
                        }

                        SetImageOpacity(fillHoverImage, 255f);
                    }
                    else if (!panelIsOpen && !isHovering && radialShouldFinish)
                    {
                        // Panel closed, not hovering, but radial is finishing
                        // Let it finish gracefully
                        if (fillHoverImage.fillAmount >= 1f)
                        {
                            // Finished - now hide it
                            fillHoverImage.fillAmount = 0f;
                            SetImageOpacity(fillHoverImage, 0f);
                            radialShouldFinish = false;
                        }
                        else
                        {
                            SetImageOpacity(fillHoverImage, 255f);
                        }
                    }
                    else if (!panelIsOpen && !isHovering && !radialShouldFinish)
                    {
                        // Panel closed, not hovering, no active animation
                        if (radialTween == null || !radialTween.IsActive())
                        {
                            // No animation, hide radial
                            fillHoverImage.fillAmount = 0f;
                            SetImageOpacity(fillHoverImage, 0f);
                        }
                    }
                }
            }
        }

        private void OnButtonClicked()
        {
            // Prevent clicking if panel is already open
            if (targetPanel == null || targetPanel.IsOpen())
            {
                return;
            }

            // Click flash effect
            if (clickFlashCoroutine != null)
            {
                StopCoroutine(clickFlashCoroutine);
            }
            clickFlashCoroutine = StartCoroutine(ClickFlashEffect());

            // Close opposite panel first (if assigned)
            if (oppositePanelToClose != null && oppositePanelToClose.IsOpen())
            {
                oppositePanelToClose.ClosePanel();
            }

            // Open the panel
            targetPanel.OpenPanel();
        }

        private IEnumerator ClickFlashEffect()
        {
            // Flash button to 50% opacity for 0.05 seconds
            if (buttonImage != null)
            {
                SetImageOpacity(buttonImage, 127.5f); // 50% of 255
                yield return new WaitForSeconds(0.05f);
                SetImageOpacity(buttonImage, 255f); // Back to full
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovering = true;

            // Increase button brightness on hover
            if (buttonImage != null)
            {
                SetImageOpacity(buttonImage, hoverOpacity);
            }

            // Radial behavior handled in Update() based on panel state
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isHovering = false;

            // Return button to normal opacity
            if (buttonImage != null)
            {
                SetImageOpacity(buttonImage, normalOpacity);
            }

            // Signal radial to finish current cycle instead of stopping abruptly
            if (targetPanel != null && !targetPanel.IsOpen() && radialTween != null && radialTween.IsActive())
            {
                radialShouldFinish = true;
                FinishRadialGracefullyOnExit();
            }
        }

        private void StartRadialLoop()
        {
            StopRadialLoop();

            if (fillHoverImage != null)
            {
                // Start from 0 for seamless loop
                fillHoverImage.fillAmount = 0f;

                // Use InOutSine for smooth animation with instant reset
                radialTween = DOTween.Sequence()
                    .Append(fillHoverImage.DOFillAmount(1f, radialLoopDuration).SetEase(Ease.InOutSine))
                    .AppendCallback(() => fillHoverImage.fillAmount = 0f) // Reset to 0 instantly
                    .SetLoops(-1);
            }
        }

        private void FinishRadialGracefully()
        {
            // If radial is looping, convert to single completion (FASTER when panel opens)
            if (radialTween != null && radialTween.IsActive())
            {
                // Check if it's already finishing (single tween, not looping)
                // If so, let it continue
                if (radialTween.Loops() == 0)
                {
                    return; // Already finishing, don't restart
                }

                // Kill the infinite loop
                radialTween.Kill();
                radialTween = null;

                // Complete the current cycle FASTER when panel opens (active state)
                if (fillHoverImage != null)
                {
                    float currentFill = fillHoverImage.fillAmount;
                    float remainingTime = (radialLoopDuration * (1f - currentFill)) / radialFinishSpeedMultiplier;

                    // Use same ease but faster duration
                    radialTween = fillHoverImage.DOFillAmount(1f, remainingTime)
                        .SetEase(Ease.InOutSine)
                        .OnComplete(() =>
                        {
                            // Keep at 100% when finished
                            fillHoverImage.fillAmount = 1f;
                            radialTween = null;
                        });
                }
            }
            else if (fillHoverImage != null && fillHoverImage.fillAmount < 1f)
            {
                // If no active tween but fill is not complete, finish it FASTER
                float currentFill = fillHoverImage.fillAmount;
                float remainingTime = (radialLoopDuration * (1f - currentFill)) / radialFinishSpeedMultiplier;

                radialTween = fillHoverImage.DOFillAmount(1f, remainingTime)
                    .SetEase(Ease.InOutSine)
                    .OnComplete(() =>
                    {
                        fillHoverImage.fillAmount = 1f;
                        radialTween = null;
                    });
            }
        }

        private void FinishRadialGracefullyOnExit()
        {
            // Called when hover exits - finish current cycle then hide
            if (radialTween != null && radialTween.IsActive())
            {
                // Check if it's already finishing
                if (radialTween.Loops() == 0)
                {
                    return; // Already finishing
                }

                // Kill the infinite loop
                radialTween.Kill();
                radialTween = null;

                // Complete the current cycle at same speed
                if (fillHoverImage != null)
                {
                    float currentFill = fillHoverImage.fillAmount;
                    float remainingTime = radialLoopDuration * (1f - currentFill);

                    radialTween = fillHoverImage.DOFillAmount(1f, remainingTime)
                        .SetEase(Ease.InOutSine)
                        .OnComplete(() =>
                        {
                            // When finished, hide it
                            fillHoverImage.fillAmount = 0f;
                            SetImageOpacity(fillHoverImage, 0f);
                            radialShouldFinish = false;
                            radialTween = null;
                        });
                }
            }
        }

        private void StopRadialLoop()
        {
            if (radialTween != null && radialTween.IsActive())
            {
                radialTween.Kill();
                radialTween = null;
            }
        }

        private void SetImageOpacity(Image img, float opacity)
        {
            if (img != null)
            {
                Color col = img.color;
                col.a = opacity / 255f;
                img.color = col;
            }
        }

        private void OnDestroy()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(OnButtonClicked);
            }
            StopRadialLoop();
        }
    }
}