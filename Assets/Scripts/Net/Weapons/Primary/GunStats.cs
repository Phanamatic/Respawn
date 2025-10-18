using UnityEngine;

namespace Game.Net.Weapons
{
    [CreateAssetMenu(fileName = "GunStats", menuName = "Game/Weapons/GunStats")]
    public sealed class GunStats : ScriptableObject
    {
        public Game.Net.PrimaryType type;

        [Header("Ammo")]
        public int magazineSize = 30;
        public int reserveSize  = 30;

        [Header("Firing")]
        public bool automatic = true;
        [Tooltip("shots per second")] public float fireRate = 10f;
        public float damage = 12f;
        [Tooltip("for shotguns")] public int pellets = 1;
        [Tooltip("degrees cone for pellets/spread")] public float spreadDegrees = 0f;

        [Header("Reload")]
        public float reloadSeconds = 2.0f;

        [Header("Ballistics")]
        public float bulletSpeed = 40f;     // units/s
        public float bulletLifetime = 2.5f; // seconds

        [Header("Prefabs")]
        public GameObject weaponViewPrefab;   // has WeaponView
        public GameObject projectilePrefab;   // has BulletProjectile
    }
}