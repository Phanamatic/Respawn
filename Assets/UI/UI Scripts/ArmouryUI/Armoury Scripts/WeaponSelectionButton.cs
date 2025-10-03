using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace UI.Scripts
{
    /// <summary>
    /// Attach this to the Selection prefab.
    /// Displays weapon info and handles selection.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class WeaponSelectionButton : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Image weaponImage;
        [SerializeField] private TextMeshProUGUI weaponNameText;
        [SerializeField] private Image backgroundImage;

        [Header("Stats References")]
        [SerializeField] private TextMeshProUGUI damageText;
        [SerializeField] private TextMeshProUGUI ammoText;
        [SerializeField] private TextMeshProUGUI fireRateText;

        [Header("Highlight Colors")]
        [SerializeField] private Color normalColor = new Color(1f, 1f, 1f, 0.392f);
        [SerializeField] private Color selectedColor = Color.green;

        private Button _button;
        private WeaponData _weaponData;
        private ArmouryManager _armouryManager;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _button.onClick.AddListener(OnButtonClicked);
        }

        /// <summary>
        /// Initialize this button with weapon data.
        /// Called by ArmouryManager when spawning buttons.
        /// </summary>
        public void Setup(WeaponData weaponData, ArmouryManager armouryManager)
        {
            _weaponData = weaponData;
            _armouryManager = armouryManager;

            if (weaponImage != null)
                weaponImage.sprite = weaponData.weaponIcon;

            if (weaponNameText != null)
                weaponNameText.text = weaponData.weaponName;

            // Populate stats
            if (damageText != null)
                damageText.text = weaponData.damage.ToString("F0");

            if (ammoText != null)
                ammoText.text = weaponData.ammo.ToString();

            if (fireRateText != null)
                fireRateText.text = weaponData.fireRate.ToString("F1");

            UpdateHighlight(false);
        }

        /// <summary>
        /// Updates the visual highlight state.
        /// </summary>
        public void UpdateHighlight(bool isSelected)
        {
            if (backgroundImage != null)
            {
                backgroundImage.color = isSelected ? selectedColor : normalColor;
            }
        }

        private void OnButtonClicked()
        {
            if (_armouryManager != null && _weaponData != null)
            {
                _armouryManager.SelectWeapon(_weaponData);
            }
        }

        private void OnDestroy()
        {
            if (_button != null)
                _button.onClick.RemoveListener(OnButtonClicked);
        }

        public WeaponData GetWeaponData() => _weaponData;
    }
}
