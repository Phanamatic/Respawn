using UnityEngine;

using Game.Net; // for PrimaryType / SecondaryType / UtilityType

namespace UI.Scripts
{
    /// <summary>
    /// ScriptableObject containing all data for a weapon.
    /// Create instances via: Right-click in Project > Create > Weapon Data
    /// </summary>
    [CreateAssetMenu(fileName = "New Weapon", menuName = "Weapon Data", order = 1)]
    public class WeaponData : ScriptableObject
    {
        [Header("Basic Info")]
        public string weaponName;
        public Sprite weaponIcon;

        [Header("Category")]
        public WeaponCategory category;

        [Header("Enum Binding (used by Loadout)")]
        public PrimaryType primaryType;       // used if category == Primary
        public SecondaryType secondaryType;   // used if category == Secondary
        public UtilityType utilityType;       // used if category == Utility

        [Header("Stats")]
        public float damage;
        public int ammo;
        public float fireRate;

#if UNITY_EDITOR
        // Auto-correct category so all Utility items appear even if authoring missed the enum.
        private void OnValidate()
        {
            if (primaryType != PrimaryType.None)           category = UI.Scripts.WeaponCategory.Primary;
            else if (secondaryType != SecondaryType.None)  category = UI.Scripts.WeaponCategory.Secondary;
            else if (utilityType != UtilityType.None)      category = UI.Scripts.WeaponCategory.Utility;
            // Melee stays as authored.
        }
#endif
    }

    public enum WeaponCategory
    {
        Primary,
        Secondary,
        Melee,
        Utility
    }
}
