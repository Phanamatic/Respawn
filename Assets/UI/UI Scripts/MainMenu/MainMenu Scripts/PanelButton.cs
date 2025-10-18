using UnityEngine;
using UnityEngine.UI;

namespace UI.Scripts
{
    [RequireComponent(typeof(Button))]
    public class PanelButton : MonoBehaviour
    {
        [Header("Panel Reference")]
        [SerializeField] private SlidingPanel targetPanel;

        private Button button;

        private void Start()
        {
            button = GetComponent<Button>();
            button.onClick.AddListener(OnButtonClicked);
        }

        private void OnButtonClicked()
        {
            if (targetPanel != null)
            {
                targetPanel.OpenPanel();
            }
        }

        private void OnDestroy()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(OnButtonClicked);
            }
        }
    }
}