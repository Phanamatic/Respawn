using UnityEngine;
using UnityEngine.UI;

namespace UI.Scripts
{
    /// <summary>
    /// Attach to PrimaryBtn, SecondaryBtn, MeleeBtn, and UtilityBtn.
    /// Shows the equipped weapon icon and triggers category selection.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class WeaponCategoryButton : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private WeaponCategory category;

        [Header("UI References")]
        [SerializeField] private Image weaponIconImage;

        [Header("Highlight")]
        [SerializeField] private Image highlightBorder;
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color selectedColor = Color.green;

        private Button _button;
        private ArmouryManager _armouryManager;

        private void Awake()
        {
            _button = GetComponent<Button>();
        }

        private void Start()
        {
            // Find ArmouryManager in parent hierarchy
            _armouryManager = GetComponentInParent<ArmouryManager>();

            if (_armouryManager == null)
            {
                Debug.LogError($"WeaponCategoryButton on {gameObject.name} couldn't find ArmouryManager in parent!");
                return;
            }

            // Register this button with the manager
            _armouryManager.RegisterCategoryButton(this);
        }

        /// <summary>
        /// Updates the weapon icon displayed on this button.
        /// </summary>
        public void UpdateWeaponIcon(Sprite weaponIcon)
        {
            if (weaponIconImage != null)
            {
                weaponIconImage.sprite = weaponIcon;
                weaponIconImage.enabled = weaponIcon != null;
            }
        }

        /// <summary>
        /// Highlights this button to show it's selected.
        /// </summary>
        public void SetHighlighted(bool highlighted)
        {
            if (highlightBorder != null)
            {
                highlightBorder.color = highlighted ? selectedColor : normalColor;
            }
        }

        public WeaponCategory GetCategory() => category;
    }
}
