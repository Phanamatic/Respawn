using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using DG.Tweening;

namespace Game.UI.MainMenu
{
    /// <summary>
    /// Smooth button transitions with DOTween
    /// Handles Default, Hover, and Pressed states with smooth animations
    /// </summary>
    [RequireComponent(typeof(Button))]
    [RequireComponent(typeof(Image))]
    public class SmoothButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [Header("Button Sprites")]
        [Tooltip("Default/Normal state sprite")]
        [SerializeField] private Sprite defaultSprite;

        [Tooltip("Hover state sprite")]
        [SerializeField] private Sprite hoverSprite;

        [Tooltip("Pressed/Clicked state sprite")]
        [SerializeField] private Sprite pressedSprite;

        [Header("Transition Settings")]
        [Tooltip("Duration of fade transition between states")]
        [SerializeField] private float fadeDuration = 0.15f;

        [Tooltip("Enable scale effect on hover")]
        [SerializeField] private bool useScaleEffect = true;

        [Tooltip("Scale multiplier on hover (e.g., 1.05 = 5% larger)")]
        [SerializeField] private float hoverScale = 1.05f;

        [Tooltip("Scale animation duration")]
        [SerializeField] private float scaleDuration = 0.2f;

        [Tooltip("Ease type for scale animation")]
        [SerializeField] private Ease scaleEase = Ease.OutBack;

        [Header("Optional Effects")]
        [Tooltip("Enable color tint on hover")]
        [SerializeField] private bool useColorTint = false;

        [Tooltip("Color tint on hover")]
        [SerializeField] private Color hoverTint = Color.white;

        private Image buttonImage;
        private Button button;
        private Vector3 originalScale;
        private Color originalColor;
        private ButtonState currentState = ButtonState.Default;
        private bool isHovering = false;
        private bool isPressed = false;

        private enum ButtonState
        {
            Default,
            Hover,
            Pressed
        }

        private void Awake()
        {
            buttonImage = GetComponent<Image>();
            button = GetComponent<Button>();
            originalScale = transform.localScale;
            originalColor = buttonImage.color;

            // Set default sprite
            if (defaultSprite != null)
            {
                buttonImage.sprite = defaultSprite;
            }

            // Disable Unity's built-in button transition
            button.transition = Selectable.Transition.None;
        }

        private void OnEnable()
        {
            // Reset to default state when enabled
            SetState(ButtonState.Default, immediate: true);
        }

        private void OnDisable()
        {
            // Kill any active tweens
            buttonImage.DOKill();
            transform.DOKill();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!button.interactable) return;

            isHovering = true;
            if (!isPressed)
            {
                SetState(ButtonState.Hover);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!button.interactable) return;

            isHovering = false;
            if (!isPressed)
            {
                SetState(ButtonState.Default);
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!button.interactable) return;

            isPressed = true;
            SetState(ButtonState.Pressed);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!button.interactable) return;

            isPressed = false;

            // Return to hover if still hovering, otherwise default
            if (isHovering)
            {
                SetState(ButtonState.Hover);
            }
            else
            {
                SetState(ButtonState.Default);
            }
        }

        private void SetState(ButtonState newState, bool immediate = false)
        {
            if (currentState == newState && !immediate) return;

            currentState = newState;

            // Kill any active tweens
            buttonImage.DOKill();
            transform.DOKill();

            // Get target sprite
            Sprite targetSprite = GetSpriteForState(newState);

            // Transition to new sprite
            if (targetSprite != null)
            {
                if (immediate)
                {
                    buttonImage.sprite = targetSprite;
                }
                else
                {
                    TransitionToSprite(targetSprite);
                }
            }

            // Handle scale effect
            if (useScaleEffect)
            {
                Vector3 targetScale = newState == ButtonState.Hover ? originalScale * hoverScale : originalScale;

                if (immediate)
                {
                    transform.localScale = targetScale;
                }
                else
                {
                    transform.DOScale(targetScale, scaleDuration).SetEase(scaleEase);
                }
            }

            // Handle color tint
            if (useColorTint)
            {
                Color targetColor = newState == ButtonState.Hover ? hoverTint : originalColor;

                if (immediate)
                {
                    buttonImage.color = targetColor;
                }
                else
                {
                    buttonImage.DOColor(targetColor, fadeDuration);
                }
            }
        }

        private Sprite GetSpriteForState(ButtonState state)
        {
            switch (state)
            {
                case ButtonState.Hover:
                    return hoverSprite != null ? hoverSprite : defaultSprite;
                case ButtonState.Pressed:
                    return pressedSprite != null ? pressedSprite : (hoverSprite != null ? hoverSprite : defaultSprite);
                case ButtonState.Default:
                default:
                    return defaultSprite;
            }
        }

        private void TransitionToSprite(Sprite newSprite)
        {
            // Smooth cross-fade transition between sprites
            buttonImage.DOFade(0f, fadeDuration * 0.5f)
                .OnComplete(() =>
                {
                    buttonImage.sprite = newSprite;
                    buttonImage.DOFade(1f, fadeDuration * 0.5f);
                });
        }

        /// <summary>
        /// Manually set button sprites at runtime
        /// </summary>
        public void SetSprites(Sprite defaultSpr, Sprite hoverSpr, Sprite pressedSpr)
        {
            defaultSprite = defaultSpr;
            hoverSprite = hoverSpr;
            pressedSprite = pressedSpr;

            if (currentState == ButtonState.Default)
            {
                buttonImage.sprite = defaultSprite;
            }
        }

        /// <summary>
        /// Enable or disable the button
        /// </summary>
        public void SetInteractable(bool interactable)
        {
            button.interactable = interactable;

            if (!interactable)
            {
                SetState(ButtonState.Default, immediate: true);
            }
        }

        private void OnDestroy()
        {
            // Clean up tweens
            buttonImage.DOKill();
            transform.DOKill();
        }
    }
}
