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
        [SerializeField] private int defaultPanelIndex = 0;

        private void Start()
        {
            SetupButtons();
            ShowPanel(defaultPanelIndex);
        }

        private void SetupButtons()
        {
            for (int i = 0; i < panelButtons.Count; i++)
            {
                int index = i; // Capture for lambda
                if (panelButtons[i].button != null)
                {
                    panelButtons[i].button.onClick.AddListener(() => ShowPanel(index));
                }
            }
        }

        public void ShowPanel(int index)
        {
            if (index < 0 || index >= panelButtons.Count) return;

            // Hide all panels
            foreach (var panelButton in panelButtons)
            {
                if (panelButton.panel != null)
                {
                    panelButton.panel.SetActive(false);
                }
            }

            // Show selected panel
            if (panelButtons[index].panel != null)
            {
                panelButtons[index].panel.SetActive(true);
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