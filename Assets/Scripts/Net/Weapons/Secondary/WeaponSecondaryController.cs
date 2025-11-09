using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-authoritative secondary weapon controller living on the player.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class WeaponSecondaryController : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] PlayerWeaponSockets sockets;

        [Header("UI Replication")]
        public NetworkVariable<int> magazineAmmo = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<int> reserveAmmo = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<bool> isReloading = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<float> reloadProgress = new(writePerm: NetworkVariableWritePermission.Server); // 0..1
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        SecondaryStats _stats;           // current gun
        [SerializeField] SecondaryStats pistolStats;
        [SerializeField] SecondaryStats machinePistolStats;

        float _fireCooldown;
        float _reloadRemain;       // seconds left when paused
        bool _reloadPaused;

        // Replicate selected secondary type so clients can build local view
        readonly NetworkVariable<byte> _netSecondaryType =
            new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Local-only visuals
        WeaponView _view;
        Game.Net.PlayerNetwork _player;
        bool _hasEquippedSecondary;

        const int InfiniteReserve = -1;
    const float ProjectileLifetimeSeconds = 10f;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
        }

        // ====== API ======
        public void Equip(Game.Net.SecondaryType secondaryType, SecondaryStats stats)
        {
            if (!IsServer) { RequestEquipServerRpc((byte)secondaryType); }
            else ServerEquip(secondaryType, stats);
        }

        /// <summary>Show or hide the local weapon view. Called when active slot changes.</summary>
        public void SetVisible(bool visible)
        {
            if (_view && _view.gameObject)
            {
                _view.gameObject.SetActive(visible);
            }
        }

        public void OnSprintChanged(bool on) { if (isReloading.Value) _reloadPaused = on; }
        public void OnDashChanged(bool on) { if (isReloading.Value) _reloadPaused = on; }

        public void FireHeld(bool on)
        {
            if (!IsOwner) return;
            if (on) RequestFireStartServerRpc(); else RequestFireStopServerRpc();
        }

        public void RequestReload()
        {
            if (!IsOwner) return;
            RequestReloadServerRpc();
        }

        // ====== Server logic ======
        [ServerRpc] void RequestEquipServerRpc(byte secondary) { ServerEquip((Game.Net.SecondaryType)secondary, null); }

        void ServerEquip(Game.Net.SecondaryType t, SecondaryStats provided)
        {
            _netSecondaryType.Value = (byte)t;
            var nextStats = provided ?? LookupAssigned(t) ?? GetDefaults(t);
            if (nextStats == null)
            {
                _stats = null;
                _hasEquippedSecondary = false;
                magazineAmmo.Value = 0;
                reserveAmmo.Value = 0;
                isReloading.Value = false;
                reloadProgress.Value = 0f;
                equippedWeaponName.Value = "";
                _reloadRemain = 0f;
                _fireCooldown = 0f;
                RebuildLocalViewClientRpc();
                return;
            }

            bool newGun = !_hasEquippedSecondary || _stats == null || _stats.type != nextStats.type;
            _stats = nextStats;

            if (newGun)
            {
                magazineAmmo.Value = Mathf.Max(0, _stats.magazineSize);
                reserveAmmo.Value = InfiniteReserve;
                isReloading.Value = false;
                reloadProgress.Value = 0f;
                equippedWeaponName.Value = t.ToString();
                _reloadRemain = 0f;
                _hasEquippedSecondary = true;
            }

            _fireCooldown = 0f;

            // Rebuild local visuals on all clients
            RebuildLocalViewClientRpc();
        }

        [ServerRpc] void RequestFireStartServerRpc(ServerRpcParams p = default) { _firing = true; }
        [ServerRpc] void RequestFireStopServerRpc(ServerRpcParams p = default) { _firing = false; }

        [ServerRpc] void RequestReloadServerRpc(ServerRpcParams p = default)
        {
            if (isReloading.Value) return;
            if (magazineAmmo.Value >= _stats.magazineSize) return;
            if (!HasReserveAmmo()) return;

            isReloading.Value = true;
            _reloadPaused = false;
            _reloadRemain = _stats.reloadSeconds;
            reloadProgress.Value = 0f;
        }

        bool _firing;
        void Update()
        {
            if (!IsServer || _stats == null) return;

            // Reload timing with pause/resume
            if (isReloading.Value)
            {
                if (_reloadPaused) return;

                var dt = Time.deltaTime;
                _reloadRemain -= dt;
                reloadProgress.Value = 1f - Mathf.Clamp01(_reloadRemain / Mathf.Max(0.001f, _stats.reloadSeconds));

                if (_reloadRemain <= 0f)
                {
                    int need = Mathf.Max(0, _stats.magazineSize - magazineAmmo.Value);
                    int take = need;
                    if (reserveAmmo.Value >= 0)
                    {
                        take = Mathf.Min(need, reserveAmmo.Value);
                        reserveAmmo.Value -= take;
                    }
                    magazineAmmo.Value += take;
                    isReloading.Value = false;
                    reloadProgress.Value = 1f;
                }
                return;
            }

            // Fire cadence
            if (_firing)
            {
                _fireCooldown -= Time.deltaTime;
                if (_fireCooldown <= 0f)
                {
                    TryFireOnce();
                    _fireCooldown = _stats.automatic ? 1f / Mathf.Max(0.01f, _stats.fireRate) : 999f;
                }
            }
            else
            {
                _fireCooldown = Mathf.Min(_fireCooldown, 0f);
            }
        }

        void TryFireOnce()
        {
            if (magazineAmmo.Value <= 0)
            {
                // auto-reload if we have reserve
                if (HasReserveAmmo()) { RequestReloadServerRpc(); }
                return;
            }

            magazineAmmo.Value--;

            // Spawn projectile(s)
            int count = Mathf.Max(1, _stats.pellets);
            for (int i = 0; i < count; i++)
            {
                var dir = GetAimDir();
                if (_stats.spreadDegrees > 0f)
                    dir = Quaternion.Euler(0f, Random.Range(-_stats.spreadDegrees, _stats.spreadDegrees), 0f) * dir;

                var pos = GetMuzzleWorld();
                var rot = Quaternion.LookRotation(dir, Vector3.up);

                // Nudge forward to avoid self-collision, then ignore shooter colliders.
                pos += dir * 0.3f;
                var go = Instantiate(_stats.projectilePrefab, pos, rot);
                var projCol = go.GetComponent<Collider>();
                if (projCol)
                {
                    var myCols = GetComponentsInParent<Collider>(true);
                    for (int c = 0; c < myCols.Length; c++)
                        if (myCols[c]) Physics.IgnoreCollision(projCol, myCols[c], true);
                }

                var nob = go.GetComponent<NetworkObject>();
                var proj = go.GetComponent<BulletProjectile>();
                if (proj)
                {
                    var owner = _player ? _player : GetComponent<Game.Net.PlayerNetwork>();
                    var ownerTeam = owner ? owner.GetTeam() : Game.Net.TeamId.A;
                    proj.ConfigureServer(_stats.bulletSpeed, ProjectileLifetimeSeconds, _stats.damage, OwnerClientId, ownerTeam, owner);
                }
                if (nob) nob.Spawn(true);
            }
        }

        Vector3 GetAimDir()
        {
            // Horizontal forward of the player
            var fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            return fwd.normalized;
        }

        Vector3 GetMuzzleWorld()
        {
            // If we have a local view on server, use it. Else use sockets.front.
            if (_view && _view.muzzle) return _view.muzzle.position;
            if (sockets && sockets.front) return sockets.front.position;
            return transform.position + GetAimDir() * 0.5f;
        }

        bool HasReserveAmmo()
        {
            return reserveAmmo.Value < 0 || reserveAmmo.Value > 0;
        }

        // ====== Client visuals ======
        [ClientRpc] void RebuildLocalViewClientRpc()
        {
            if (!IsOwner) return;

            if (sockets && sockets.handMount)
            {
                for (int i = sockets.handMount.childCount - 1; i >= 0; i--)
                    Destroy(sockets.handMount.GetChild(i).gameObject);
            }

            if (_view) Destroy(_view.gameObject);
            _view = null;

            var secondaryType = (Game.Net.SecondaryType)_netSecondaryType.Value;
            if (secondaryType == Game.Net.SecondaryType.None)
            {
                return;
            }
            var localStats = LookupAssigned(secondaryType) ?? GetDefaults(secondaryType);

            if (!sockets)
            {
                Debug.LogWarning("[Weapons] PlayerWeaponSockets missing on player. Cannot attach WeaponView.");
                return;
            }
            if (!sockets.handMount)
            {
                Debug.LogWarning("[Weapons] sockets.handMount not assigned. Cannot attach WeaponView.");
                return;
            }
            if (localStats == null || !localStats.weaponViewPrefab)
            {
                Debug.LogWarning($"[Weapons] No WeaponView prefab set for {secondaryType}. Assign SecondaryStats.weaponViewPrefab.");
                return;
            }

            // Ensure only one weapon view exists under the Hand Mount
            if (sockets && sockets.handMount)
            {
                for (int i = sockets.handMount.childCount - 1; i >= 0; i--)
                    Destroy(sockets.handMount.GetChild(i).gameObject);
            }

            var go = Instantiate(localStats.weaponViewPrefab);
            go.name = $"{secondaryType}_View(Local)";
            _view = go.GetComponent<WeaponView>();

            var t = go.transform;
            if (_view && _view.grip)
            {
                t.SetParent(sockets.handMount, false);
                t.position = sockets.handMount.position;
                t.rotation = sockets.handMount.rotation;
            }
            else
            {
                t.SetParent(transform, false);
            }

            if (_view) StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
        }
// [Weapons] Clears stale children before spawning Secondary local view.

        // ====== Defaults if no SO assigned ======
        static readonly Dictionary<Game.Net.SecondaryType, SecondaryStats> _defaults = new();

        SecondaryStats LookupAssigned(Game.Net.SecondaryType t)
        {
            return t switch
            {
                Game.Net.SecondaryType.Pistol => pistolStats,
                Game.Net.SecondaryType.MachinePistol => machinePistolStats,
                _ => null
            };
        }

        SecondaryStats GetDefaults(Game.Net.SecondaryType t)
        {
            if (t == Game.Net.SecondaryType.None) return null;
            if (_defaults.TryGetValue(t, out var s)) return s;

            var g = ScriptableObject.CreateInstance<SecondaryStats>();
            g.type = t;

            switch (t)
            {
                case Game.Net.SecondaryType.Pistol:
                    g.magazineSize = 12; g.reserveSize = 24; g.automatic = false; g.fireRate = 4f; g.damage = 25f; g.bulletSpeed = 40f; g.reloadSeconds = 1.5f; break;
                case Game.Net.SecondaryType.MachinePistol:
                    g.magazineSize = 20; g.reserveSize = 40; g.automatic = true; g.fireRate = 8f; g.damage = 15f; g.bulletSpeed = 38f; g.reloadSeconds = 1.8f; break;
                default:
                    g.magazineSize = 12; g.reserveSize = 24; g.automatic = false; g.fireRate = 4f; g.damage = 25f; g.bulletSpeed = 40f; g.reloadSeconds = 1.5f; break;
            }

            if (!g.projectilePrefab)
            {
                Debug.LogError($"[Weapons] Missing projectile prefab for {t}. Assign a prefab asset and register it in NetworkManager → Network Prefabs.");
            }

            if (!g.weaponViewPrefab)
            {
                Debug.LogWarning($"[Weapons] Missing WeaponView prefab for {t}. Assign SecondaryStats.weaponViewPrefab.");
            }

            _defaults[t] = g;
            return g;
        }
    }
}
