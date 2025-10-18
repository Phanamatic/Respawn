using UnityEngine;
using UnityEngine.UI;
using System.Collections;

namespace UI.Scripts
{
    [RequireComponent(typeof(Button))]
    public class GameObjectToggle : MonoBehaviour
    {
        [Header("Target Settings")]
        [SerializeField] private GameObject targetGameObject;
        [SerializeField] private Animator targetAnimator;
        [SerializeField] private string openAnimationName = "NewsAnim";
        [SerializeField] private string closeAnimationName = "NewsAnim_Reverse";

        [Header("Navigation Dots Control")]
        [SerializeField] private NewsCarousel newsCarousel;

        private Button button;
        private bool isExpanded = false;

        private void Start()
        {
            button = GetComponent<Button>();
            button.onClick.AddListener(ToggleTarget);

            // Start collapsed (inactive)
            if (targetGameObject != null)
            {
                targetGameObject.SetActive(false);
            }
        }

        private void ToggleTarget()
        {
            if (targetGameObject == null) return;

            if (isExpanded)
            {
                CollapseTarget();
            }
            else
            {
                ExpandTarget();
            }
        }

        private void ExpandTarget()
        {
            if (targetGameObject == null) return;

            // Activate GameObject
            targetGameObject.SetActive(true);
            isExpanded = true;

            // Show navigation dots
            if (newsCarousel != null)
            {
                newsCarousel.ShowNavigationDots();
            }

            // Play open animation
            if (targetAnimator != null)
            {
                targetAnimator.Play(openAnimationName);
            }
        }

        private void CollapseTarget()
        {
            if (targetGameObject == null) return;

            isExpanded = false;

            // Hide navigation dots
            if (newsCarousel != null)
            {
                newsCarousel.HideNavigationDots();
            }

            // Play close animation
            if (targetAnimator != null)
            {
                targetAnimator.Play(closeAnimationName);
            }
        }

        private void OnDestroy()
        {
            if (button != null)
                button.onClick.RemoveListener(ToggleTarget);
        }
    }
}