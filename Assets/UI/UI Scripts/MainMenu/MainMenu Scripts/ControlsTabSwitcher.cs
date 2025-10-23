using UnityEngine;
using TMPro;

namespace UI.Scripts
{
    public class ControlsTabSwitcher : MonoBehaviour
    {
        [System.Serializable]
        public class ControlTab
        {
            public TextMeshProUGUI headingText; // The clickable heading text (e.g., "PC" or "Console")
            // The script will automatically find and control the heading's child GameObjects
        }

        [Header("Control Tabs")]
        [SerializeField] private ControlTab pcTab;
        [SerializeField] private ControlTab consoleTab;

        [Header("Opacity Settings")]
        [SerializeField] private float activeOpacity = 1f; // Full brightness
        [SerializeField] private float inactiveOpacity = 0.5f; // Half brightness

        private void Start()
        {
            // Setup click listeners
            if (pcTab.headingText != null)
            {
                var pcButton = pcTab.headingText.gameObject.AddComponent<UnityEngine.UI.Button>();
                pcButton.transition = UnityEngine.UI.Selectable.Transition.None; // No default button visuals
                pcButton.onClick.AddListener(ShowPC);
            }

            if (consoleTab.headingText != null)
            {
                var consoleButton = consoleTab.headingText.gameObject.AddComponent<UnityEngine.UI.Button>();
                consoleButton.transition = UnityEngine.UI.Selectable.Transition.None;
                consoleButton.onClick.AddListener(ShowConsole);
            }

            // Start with PC active
            ShowPC();
        }

        public void ShowPC()
        {
            // Show PC children, hide Console children
            SetChildrenActive(pcTab.headingText, true);
            SetChildrenActive(consoleTab.headingText, false);

            // Update heading opacities
            SetHeadingOpacity(pcTab.headingText, activeOpacity);
            SetHeadingOpacity(consoleTab.headingText, inactiveOpacity);
        }

        public void ShowConsole()
        {
            // Show Console children, hide PC children
            SetChildrenActive(consoleTab.headingText, true);
            SetChildrenActive(pcTab.headingText, false);

            // Update heading opacities
            SetHeadingOpacity(consoleTab.headingText, activeOpacity);
            SetHeadingOpacity(pcTab.headingText, inactiveOpacity);
        }

        private void SetChildrenActive(TextMeshProUGUI headingText, bool active)
        {
            if (headingText == null) return;

            // Get the heading's own transform and affect its direct children
            Transform headingTransform = headingText.transform;

            // Loop through all children of the heading
            foreach (Transform child in headingTransform)
            {
                child.gameObject.SetActive(active);
            }
        }

        private void SetHeadingOpacity(TextMeshProUGUI text, float opacity)
        {
            if (text != null)
            {
                Color color = text.color;
                color.a = opacity;
                text.color = color;
            }
        }
    }
}
