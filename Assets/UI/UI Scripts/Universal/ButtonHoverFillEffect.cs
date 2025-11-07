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
    public class ButtonHoverFillEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Hover Image")]
        [SerializeField] private Image hoverImage;

        [Header("Fill Settings")]
        [SerializeField] private float fillSpeed = 2f;

        [Header("Behavior")]
        [SerializeField] private bool deactivateImageOnExit = true;

        private float targetFillAmount = 0f;
        private bool isHovering = false;

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
            // Animate fill amount
            if (hoverImage != null && hoverImage.type == Image.Type.Filled)
            {
                hoverImage.fillAmount = Mathf.MoveTowards(
                    hoverImage.fillAmount,
                    targetFillAmount,
                    fillSpeed * Time.deltaTime
                );
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovering = true;

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

        private System.Collections.IEnumerator DeactivateWhenEmpty()
        {
            // Wait until fill amount is approximately 0
            while (hoverImage != null && hoverImage.fillAmount > 0.01f)
            {
                yield return null;
            }

            // Deactivate image once empty
            if (hoverImage != null && !isHovering)
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
    }
}
