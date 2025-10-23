using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace UI.Scripts
{
    public class OptionsPanelSwitcher : MonoBehaviour
    {
        [System.Serializable]
        public class PanelButton
        {
            public Button button;
            public GameObject panel;
        }

        [Header("Panel Configuration")]
        [SerializeField] private List<PanelButton> panelButtons = new List<PanelButton>();

        [Header("Initial State")]
        [SerializeField] private bool startWithAllClosed = true;
        [SerializeField] private List<int> defaultOpenPanels = new List<int>(); // Panels to open on start

        private void Start()
        {
            SetupButtons();
            InitializePanels();
        }

        private void SetupButtons()
        {
            for (int i = 0; i < panelButtons.Count; i++)
            {
                int index = i; // Capture for lambda
                if (panelButtons[i].button != null)
                {
                    panelButtons[i].button.onClick.AddListener(() => TogglePanel(index));
                }
            }
        }

        private void InitializePanels()
        {
            if (startWithAllClosed)
            {
                // Close all panels
                foreach (var panelButton in panelButtons)
                {
                    if (panelButton.panel != null)
                    {
                        panelButton.panel.SetActive(false);
                    }
                }
            }

            // Open default panels if specified
            foreach (int index in defaultOpenPanels)
            {
                if (index >= 0 && index < panelButtons.Count && panelButtons[index].panel != null)
                {
                    panelButtons[index].panel.SetActive(true);
                }
            }
        }

        public void TogglePanel(int index)
        {
            if (index < 0 || index >= panelButtons.Count) return;

            GameObject panel = panelButtons[index].panel;
            if (panel != null)
            {
                // Toggle the panel on/off
                panel.SetActive(!panel.activeSelf);
            }
        }

        // Legacy method kept for compatibility
        public void ShowPanel(int index)
        {
            if (index < 0 || index >= panelButtons.Count) return;

            if (panelButtons[index].panel != null)
            {
                panelButtons[index].panel.SetActive(true);
            }
        }

        // New method to close a specific panel
        public void ClosePanel(int index)
        {
            if (index < 0 || index >= panelButtons.Count) return;

            if (panelButtons[index].panel != null)
            {
                panelButtons[index].panel.SetActive(false);
            }
        }

        // Close all panels
        public void CloseAllPanels()
        {
            foreach (var panelButton in panelButtons)
            {
                if (panelButton.panel != null)
                {
                    panelButton.panel.SetActive(false);
                }
            }
        }

        private void OnDestroy()
        {
            // Remove all listeners
            for (int i = 0; i < panelButtons.Count; i++)
            {
                int index = i;
                if (panelButtons[i].button != null)
                {
                    panelButtons[i].button.onClick.RemoveAllListeners();
                }
            }
        }
    }
}