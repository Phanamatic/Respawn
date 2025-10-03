using UnityEngine;

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

        [Header("Stats")]
        public float damage;
        public int ammo;
        public float fireRate;
    }

    public enum WeaponCategory
    {
        Primary,
        Secondary,
        Melee,
        Utility
    }
}
