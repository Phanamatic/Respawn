using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-authoritative primary weapon controller living on the player.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class WeaponPrimaryController : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] PlayerWeaponSockets sockets;

        [Header("UI Replication")]
        public NetworkVariable<int> magazineAmmo = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<int> reserveAmmo  = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<bool> isReloading = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<float> reloadProgress = new(writePerm: NetworkVariableWritePermission.Server); // 0..1
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        GunStats _stats;           // current gun
        [SerializeField] GunStats assaultRifleStats;
        [SerializeField] GunStats smgStats;
        [SerializeField] GunStats lmgStats;
        [SerializeField] GunStats shotgunStats;
        [SerializeField] GunStats sniperStats;

        float _fireCooldown;
        float _reloadRemain;       // seconds left when paused
        bool _reloadPaused;

        // Replicate selected primary type so clients can build local view
        readonly NetworkVariable<byte> _netPrimaryType =
            new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Local-only visuals
        WeaponView _view;
        Game.Net.PlayerNetwork _player;
        bool _hasEquippedPrimary;

        const int InfiniteReserve = -1;
        const float ProjectileLifetimeSeconds = 3f;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
        }

        // ====== API ======
        public void Equip(Game.Net.PrimaryType primaryType, GunStats stats)
        {
            if (!IsServer) { RequestEquipServerRpc((byte)primaryType); } // validate server-side
            else ServerEquip(primaryType, stats);
        }

        public void OnSprintChanged(bool on)   { if (isReloading.Value) _reloadPaused = on; }
        public void OnDashChanged(bool on)     { if (isReloading.Value) _reloadPaused = on; }

        public void FireHeld(bool on)
        {
            if (!IsOwner) return; // input from owner only
            if (on) RequestFireStartServerRpc(); else RequestFireStopServerRpc();
        }

        public void RequestReload()
        {
            if (!IsOwner) return;
            RequestReloadServerRpc();
        }

        // ====== Server logic ======
        [ServerRpc] void RequestEquipServerRpc(byte primary) { ServerEquip((Game.Net.PrimaryType)primary, null); }

        void ServerEquip(Game.Net.PrimaryType t, GunStats provided)
        {
            Debug.Log($"[Weapons] ServerEquip start cid={OwnerClientId} type={t} provided={(provided!=null)}");
            _netPrimaryType.Value = (byte)t;
            var nextStats = provided ?? LookupAssigned(t) ?? GetDefaults(t);
            if (nextStats == null)
            {
                Debug.LogWarning($"[Weapons] ServerEquip aborted: no GunStats found for {t}. Clearing state.");
                _stats = null;
                _hasEquippedPrimary = false;
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

            bool newGun = !_hasEquippedPrimary || _stats == null || _stats.type != nextStats.type;
            _stats = nextStats;

            if (newGun)
            {
                magazineAmmo.Value = Mathf.Max(0, _stats.magazineSize);
                reserveAmmo.Value  = InfiniteReserve;
                isReloading.Value = false;
                reloadProgress.Value = 0f;
                equippedWeaponName.Value = t.ToString();
                _reloadRemain = 0f;
                _hasEquippedPrimary = true;
                Debug.Log($"[Weapons] Equipped new primary={t} mag={magazineAmmo.Value} reserve={(reserveAmmo.Value < 0 ? "INF" : reserveAmmo.Value.ToString())} fireRate={_stats.fireRate} auto={_stats.automatic}");
            }
            else
            {
                Debug.Log($"[Weapons] Re-equipping same primary={t} (refresh view only).");
            }

            _fireCooldown = 0f;

            // Rebuild local visuals on all clients
            RebuildLocalViewClientRpc();
        }

        [ServerRpc] void RequestFireStartServerRpc(ServerRpcParams p = default)
        {
            _firing = true;
            Debug.Log($"[Weapons] FireStart cid={OwnerClientId} gun={_stats?.type.ToString() ?? "<null>"} mag={magazineAmmo.Value} reserve={(reserveAmmo.Value < 0 ? "INF" : reserveAmmo.Value.ToString())}");
        }
        [ServerRpc] void RequestFireStopServerRpc(ServerRpcParams p = default)
        {
            _firing = false;
            Debug.Log($"[Weapons] FireStop cid={OwnerClientId}");
        }

        [ServerRpc] void RequestReloadServerRpc(ServerRpcParams p = default)
        {
            if (isReloading.Value) { Debug.Log($"[Weapons] Reload rejected: already reloading (cid={OwnerClientId})"); return; }
            if (_stats == null)    { Debug.LogWarning($"[Weapons] Reload rejected: no stats (cid={OwnerClientId})"); return; }
            if (magazineAmmo.Value >= _stats.magazineSize)
            {
                Debug.Log($"[Weapons] Reload rejected: magazine full ({magazineAmmo.Value}/{_stats.magazineSize}) cid={OwnerClientId}");
                return;
            }
            if (!HasReserveAmmo())
            {
                Debug.Log($"[Weapons] Reload rejected: no reserve ammo cid={OwnerClientId}");
                return;
            }

            isReloading.Value = true;
            _reloadPaused = false;
            _reloadRemain = _stats.reloadSeconds;
            reloadProgress.Value = 0f;
            Debug.Log($"[Weapons] Reload start cid={OwnerClientId} mag={magazineAmmo.Value} reserve={(reserveAmmo.Value < 0 ? "INF" : reserveAmmo.Value.ToString())} t={_reloadRemain:0.00}s");
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
                    int beforeReserve = reserveAmmo.Value;
                    if (reserveAmmo.Value >= 0)
                    {
                        take = Mathf.Min(need, reserveAmmo.Value);
                        reserveAmmo.Value -= take;
                    }
                    magazineAmmo.Value += take;
                    isReloading.Value = false;
                    reloadProgress.Value = 1f;
                    Debug.Log($"[Weapons] Reload complete cid={OwnerClientId} +{take} → mag={magazineAmmo.Value}/{_stats.magazineSize} reserve={(reserveAmmo.Value < 0 ? "INF" : reserveAmmo.Value.ToString())} (was {beforeReserve})");
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
                Debug.Log($"[Weapons] Dry fire cid={OwnerClientId} gun={_stats?.type} reserve={(reserveAmmo.Value < 0 ? "INF" : reserveAmmo.Value.ToString())} -> autoReload={(HasReserveAmmo())}");
                // auto-reload if we have reserve
                if (HasReserveAmmo()) { RequestReloadServerRpc(); }
                return;
            }

            int before = magazineAmmo.Value;
            magazineAmmo.Value--;
            Debug.Log($"[Weapons] FireOne cid={OwnerClientId} gun={_stats?.type} mag {before-1}/{_stats.magazineSize} (before={before}) pellets={Mathf.Max(1,_stats.pellets)}");

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

                if (i == 0)
                    Debug.Log($"[Weapons] Spawn projectile prefab={_stats.projectilePrefab?.name ?? "<null>"} pos={pos} dir={dir} speed={_stats.bulletSpeed}");
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
            if (_view) Destroy(_view.gameObject);
            _view = null;

            var primaryType = (Game.Net.PrimaryType)_netPrimaryType.Value;
            Debug.Log($"[Weapons] RebuildLocalView (client) primary={primaryType}");
            if (primaryType == Game.Net.PrimaryType.None)
            {
                Debug.LogWarning("[Weapons] RebuildLocalView aborted: primary=None");
                return;
            }
            var localStats = LookupAssigned(primaryType) ?? GetDefaults(primaryType);

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
                Debug.LogWarning($"[Weapons] No WeaponView prefab set for {primaryType}. Assign GunStats.weaponViewPrefab.");
                return;
            }

            var go = Instantiate(localStats.weaponViewPrefab);
            go.name = $"{primaryType}_View(Local)";
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

            Debug.Log($"[Weapons] Local view spawned prefab={go.name}");
            if (_view) StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
        }

        // ====== Defaults if no SO assigned ======
        static readonly Dictionary<Game.Net.PrimaryType, GunStats> _defaults = new();

        GunStats LookupAssigned(Game.Net.PrimaryType t)
        {
            return t switch
            {
                Game.Net.PrimaryType.AR => assaultRifleStats,
                Game.Net.PrimaryType.SMG => smgStats,
                Game.Net.PrimaryType.LMG => lmgStats,
                Game.Net.PrimaryType.Shotgun => shotgunStats,
                Game.Net.PrimaryType.Sniper => sniperStats,
                _ => null
            };
        }

        GunStats GetDefaults(Game.Net.PrimaryType t)
        {
            if (t == Game.Net.PrimaryType.None) return null;
            if (_defaults.TryGetValue(t, out var s)) return s;

            var g = ScriptableObject.CreateInstance<GunStats>();
            g.type = t;

            switch (t)
            {
                case Game.Net.PrimaryType.AR:
                    g.magazineSize = 30; g.reserveSize = 30; g.automatic = true; g.fireRate = 10f; g.damage = 12f; g.bulletSpeed = 42f; g.reloadSeconds = 2.0f; break;
                case Game.Net.PrimaryType.SMG:
                    g.magazineSize = 25; g.reserveSize = 25; g.automatic = true; g.fireRate = 12f; g.damage = 9f;  g.bulletSpeed = 38f; g.reloadSeconds = 1.8f; break;
                case Game.Net.PrimaryType.LMG:
                    g.magazineSize = 60; g.reserveSize = 60; g.automatic = true; g.fireRate = 9f;  g.damage = 11f; g.bulletSpeed = 40f; g.reloadSeconds = 4.5f; break;
                case Game.Net.PrimaryType.Shotgun:
                    g.magazineSize = 8;  g.reserveSize = 8;  g.automatic = false; g.fireRate = 1.1f; g.damage = 9f;  g.pellets = 8; g.spreadDegrees = 8f; g.bulletSpeed = 32f; g.reloadSeconds = 3.2f; break;
                case Game.Net.PrimaryType.Sniper:
                    g.magazineSize = 5;  g.reserveSize = 5;  g.automatic = false; g.fireRate = 0.8f; g.damage = 60f; g.bulletSpeed = 60f; g.reloadSeconds = 3.0f; break;
                default:
                    g.magazineSize = 30; g.reserveSize = 30; g.automatic = true; g.fireRate = 10f; g.damage = 12f; g.bulletSpeed = 40f; g.reloadSeconds = 2.0f; break;
            }

            // Do NOT create runtime network prefabs; clients won't have the hash.
// Require a proper asset assigned and registered in NetworkManager.
            if (!g.projectilePrefab)
            {
                Debug.LogError($"[Weapons] Missing projectile prefab for {t}. Assign a prefab asset and register it in NetworkManager → Network Prefabs.");
            }

            // WeaponView must be a real prefab (with meshes) assigned on the GunStats.
            if (!g.weaponViewPrefab)
            {
                Debug.LogWarning($"[Weapons] Missing WeaponView prefab for {t}. Assign GunStats.weaponViewPrefab.");
            }

            _defaults[t] = g;
            return g;
        }
    }
}