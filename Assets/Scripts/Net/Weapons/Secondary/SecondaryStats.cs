using UnityEngine;

namespace Game.Net.Weapons
{
    [CreateAssetMenu(fileName = "SecondaryStats", menuName = "Game/Weapons/SecondaryStats")]
    public sealed class SecondaryStats : ScriptableObject
    {
        public Game.Net.SecondaryType type;

        [Header("Ammo")]
        public int magazineSize = 12;
        public int reserveSize = 24;

        [Header("Firing")]
        public bool automatic = false;
        [Tooltip("shots per second")] public float fireRate = 4f;
        public float damage = 25f;
        [Tooltip("for shotguns")] public int pellets = 1;
        [Tooltip("degrees cone for pellets/spread")] public float spreadDegrees = 0f;

        [Header("Reload")]
        public float reloadSeconds = 1.5f;

        [Header("Ballistics")]
        public float bulletSpeed = 40f;     // units/s
        public float bulletLifetime = 2.5f; // seconds

        [Header("Prefabs")]
        public GameObject weaponViewPrefab;   // has WeaponView
        public GameObject projectilePrefab;   // has BulletProjectile
    }
}
