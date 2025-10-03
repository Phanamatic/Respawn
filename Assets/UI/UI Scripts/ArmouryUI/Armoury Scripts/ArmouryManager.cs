using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;

namespace UI.Scripts
{
    /// <summary>
    /// Main controller for the Armoury Panel.
    /// Attach this to the "Armoury Panel" GameObject.
    /// Manages weapon selection, category buttons, and dynamic selection panel population.
    /// </summary>
    public class ArmouryManager : MonoBehaviour
    {
        [Header("Weapon Database")]
        [SerializeField] private List<WeaponData> allWeapons = new List<WeaponData>();

        [Header("Selection Panel")]
        [SerializeField] private Transform selectionContainer;
        [SerializeField] private GameObject selectionButtonPrefab;
        [SerializeField] private ScrollRect scrollRect;

        // Runtime data
        private List<WeaponCategoryButton> _categoryButtons = new List<WeaponCategoryButton>();
        private List<WeaponSelectionButton> _spawnedSelectionButtons = new List<WeaponSelectionButton>();
        private Dictionary<WeaponCategory, WeaponData> _equippedWeapons = new Dictionary<WeaponCategory, WeaponData>();
        private WeaponCategory _currentCategory = WeaponCategory.Primary;

        private void Start()
        {
            // Initialize equipped weapons with defaults (first weapon in each category)
            InitializeDefaultWeapons();

            // Show primary weapons by default
            ShowWeaponsForCategory(WeaponCategory.Primary);
        }

        /// <summary>
        /// Called by WeaponCategoryButton to register itself.
        /// </summary>
        public void RegisterCategoryButton(WeaponCategoryButton button)
        {
            if (!_categoryButtons.Contains(button))
            {
                _categoryButtons.Add(button);

                // Update button with equipped weapon icon if available
                if (_equippedWeapons.TryGetValue(button.GetCategory(), out WeaponData equippedWeapon))
                {
                    button.UpdateWeaponIcon(equippedWeapon.weaponIcon);
                }
            }
        }

        // Unity Button OnClick event wrappers
        public void ShowPrimaryWeapons() => ShowWeaponsForCategory(WeaponCategory.Primary);
        public void ShowSecondaryWeapons() => ShowWeaponsForCategory(WeaponCategory.Secondary);
        public void ShowMeleeWeapons() => ShowWeaponsForCategory(WeaponCategory.Melee);
        public void ShowUtilityWeapons() => ShowWeaponsForCategory(WeaponCategory.Utility);

        /// <summary>
        /// Shows weapons for the selected category.
        /// Called when a category button is clicked.
        /// </summary>
        public void ShowWeaponsForCategory(WeaponCategory category)
        {
            _currentCategory = category;

            // Clear existing selection buttons
            ClearSelectionButtons();

            // Get weapons for this category
            List<WeaponData> categoryWeapons = allWeapons.Where(w => w.category == category).ToList();

            // Spawn selection buttons
            foreach (WeaponData weapon in categoryWeapons)
            {
                GameObject buttonObj = Instantiate(selectionButtonPrefab, selectionContainer);
                WeaponSelectionButton button = buttonObj.GetComponent<WeaponSelectionButton>();

                if (button != null)
                {
                    button.Setup(weapon, this);

                    // Highlight if this is the equipped weapon
                    bool isEquipped = _equippedWeapons.TryGetValue(category, out WeaponData equippedWeapon)
                                      && equippedWeapon == weapon;
                    button.UpdateHighlight(isEquipped);

                    _spawnedSelectionButtons.Add(button);
                }
            }

            // Update category button highlights
            UpdateCategoryButtonHighlights();

            // Reset scroll position
            if (scrollRect != null)
            {
                scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        /// <summary>
        /// Called when a weapon selection button is clicked.
        /// </summary>
        public void SelectWeapon(WeaponData weaponData)
        {
            // Update equipped weapon for this category
            _equippedWeapons[weaponData.category] = weaponData;

            // Update the category button icon
            WeaponCategoryButton categoryButton = _categoryButtons.Find(b => b.GetCategory() == weaponData.category);
            if (categoryButton != null)
            {
                categoryButton.UpdateWeaponIcon(weaponData.weaponIcon);
            }

            // Update highlights on selection buttons
            foreach (WeaponSelectionButton button in _spawnedSelectionButtons)
            {
                bool isSelected = button.GetWeaponData() == weaponData;
                button.UpdateHighlight(isSelected);
            }

            Debug.Log($"Selected weapon: {weaponData.weaponName} ({weaponData.category})");
        }

        private void ClearSelectionButtons()
        {
            foreach (WeaponSelectionButton button in _spawnedSelectionButtons)
            {
                if (button != null)
                    Destroy(button.gameObject);
            }

            _spawnedSelectionButtons.Clear();
        }

        private void UpdateCategoryButtonHighlights()
        {
            foreach (WeaponCategoryButton button in _categoryButtons)
            {
                button.SetHighlighted(button.GetCategory() == _currentCategory);
            }
        }

        private void InitializeDefaultWeapons()
        {
            // Set first weapon of each category as default equipped
            foreach (WeaponCategory category in System.Enum.GetValues(typeof(WeaponCategory)))
            {
                WeaponData firstWeapon = allWeapons.FirstOrDefault(w => w.category == category);
                if (firstWeapon != null)
                {
                    _equippedWeapons[category] = firstWeapon;
                }
            }
        }

        /// <summary>
        /// Gets the currently equipped weapon for a category.
        /// Useful for other systems that need to know what's equipped.
        /// </summary>
        public WeaponData GetEquippedWeapon(WeaponCategory category)
        {
            return _equippedWeapons.TryGetValue(category, out WeaponData weapon) ? weapon : null;
        }

        /// <summary>
        /// Gets all equipped weapons.
        /// </summary>
        public Dictionary<WeaponCategory, WeaponData> GetAllEquippedWeapons()
        {
            return new Dictionary<WeaponCategory, WeaponData>(_equippedWeapons);
        }
    }
}
