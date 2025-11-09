using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace UI.Scripts.Universal
{
    /// <summary>
    /// Button hover effect that activates an image and animates its fill amount.
    /// Attach this to a Button GameObject.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class ButtonHoverFillEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        [Header("Hover Image")]
        [SerializeField] private Image hoverImage;

        [Header("Fill Settings")]
        [SerializeField] private float fillSpeed = 2f;

        [Header("Behavior")]
        [SerializeField] private bool deactivateImageOnExit = true;

        [Header("Click Settings")]
        [SerializeField] private bool enableClickFill = true;
        [SerializeField] private float clickFillDuration = 5f;

        private float targetFillAmount = 0f;
        private bool isHovering = false;
        private bool isClickedFull = false;
        private Coroutine clickFillCoroutine;

        private void Start()
        {
            // Initialize - ensure image is off and fill is at 0
            if (hoverImage != null)
            {
                if (deactivateImageOnExit)
                {
                    hoverImage.gameObject.SetActive(false);
                }
                hoverImage.fillAmount = 0f;
            }
        }

        private void Update()
        {
            // Animate fill amount (unless clicked and held full)
            if (hoverImage != null && hoverImage.type == Image.Type.Filled)
            {
                // If clicked, keep at full
                if (isClickedFull)
                {
                    hoverImage.fillAmount = 1f;
                }
                else
                {
                    hoverImage.fillAmount = Mathf.MoveTowards(
                        hoverImage.fillAmount,
                        targetFillAmount,
                        fillSpeed * Time.deltaTime
                    );
                }
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovering = true;

            // Don't override if clicked
            if (isClickedFull) return;

            if (hoverImage != null)
            {
                // Activate image
                hoverImage.gameObject.SetActive(true);

                // Set target fill to 1 (full)
                targetFillAmount = 1f;
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isHovering = false;

            // Don't override if clicked
            if (isClickedFull) return;

            if (hoverImage != null)
            {
                // Set target fill to 0 (empty)
                targetFillAmount = 0f;

                // Optionally deactivate image when not hovering
                if (deactivateImageOnExit)
                {
                    // Wait for fill to reach 0 before deactivating
                    StartCoroutine(DeactivateWhenEmpty());
                }
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!enableClickFill) return;

            // Stop any existing click fill coroutine
            if (clickFillCoroutine != null)
            {
                StopCoroutine(clickFillCoroutine);
            }

            // Start click fill effect
            clickFillCoroutine = StartCoroutine(ClickFillEffect());
        }

        private System.Collections.IEnumerator ClickFillEffect()
        {
            // Set flag to keep fill at full
            isClickedFull = true;

            // Ensure image is active and fill is full
            if (hoverImage != null)
            {
                hoverImage.gameObject.SetActive(true);
                hoverImage.fillAmount = 1f;
            }

            // Wait for the duration
            yield return new WaitForSeconds(clickFillDuration);

            // Resume normal behavior
            isClickedFull = false;

            // If not hovering, start emptying
            if (!isHovering)
            {
                targetFillAmount = 0f;

                if (deactivateImageOnExit)
                {
                    StartCoroutine(DeactivateWhenEmpty());
                }
            }
        }

        private System.Collections.IEnumerator DeactivateWhenEmpty()
        {
            // Wait until fill amount is approximately 0
            while (hoverImage != null && hoverImage.fillAmount > 0.01f)
            {
                yield return null;
            }

            // Deactivate image once empty
            if (hoverImage != null && !isHovering && !isClickedFull)
            {
                hoverImage.fillAmount = 0f;
                hoverImage.gameObject.SetActive(false);
            }
        }

        // Public methods for manual control
        public void ForceHover()
        {
            OnPointerEnter(null);
        }

        public void ForceExit()
        {
            OnPointerExit(null);
        }

        public void ForceClick()
        {
            OnPointerClick(null);
        }
    }
}
