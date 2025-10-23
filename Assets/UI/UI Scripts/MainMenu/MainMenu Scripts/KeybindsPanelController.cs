using UnityEngine;

namespace UI.Scripts
{
    public class KeybindsPanelController : MonoBehaviour
    {
        [Header("Panel References")]
        [SerializeField] private SlidingPanel optionsPanel;
        [SerializeField] private SlidingPanel keybindsPanel;

        // Called by Edit Keybinds button
        public void OpenKeybinds()
        {
            if (optionsPanel != null)
            {
                optionsPanel.ClosePanel();
            }

            if (keybindsPanel != null)
            {
                keybindsPanel.OpenPanel();
            }
        }

        // Called by Back button on Keybinds panel
        public void CloseKeybinds()
        {
            if (keybindsPanel != null)
            {
                keybindsPanel.ClosePanel();
            }

            if (optionsPanel != null)
            {
                optionsPanel.OpenPanel();
            }
        }
    }
}
