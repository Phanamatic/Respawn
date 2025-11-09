using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-authoritative utility/throwable weapon controller living on the player.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class WeaponUtilityController : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] PlayerWeaponSockets sockets;

        [Header("UI Replication")]
        public NetworkVariable<int> ammoCount = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        // Replicate selected utility type so clients can build local view
        readonly NetworkVariable<byte> _netUtilityType =
            new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        [Header("Throwable Prefabs")]
        [SerializeField] GameObject fragGrenadePrefab;
        [SerializeField] GameObject smokePrefab;
        [SerializeField] GameObject stunPrefab;

        [Header("Utility View Prefabs (for hand mount)")]
        [SerializeField] GameObject grenadeViewPrefab;
        [SerializeField] GameObject smokeViewPrefab;
        [SerializeField] GameObject stunViewPrefab;

        [Header("Settings")]
        [SerializeField] float throwForce = 20f;
        [SerializeField] float throwAngle = 30f; // degrees upward

        Game.Net.PlayerNetwork _player;
        bool _hasEquippedUtility;
        WeaponView _view;
        [SerializeField] private Transform _viewLocal;        // Hand Mount
        private int _lastRebuildFrame = -1;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
            if (!sockets) sockets = GetComponent<PlayerWeaponSockets>(); // runtime fallback
        }

        // ====== API ======
        public void Equip(Game.Net.UtilityType utilityType)
        {
            if (!IsServer) { RequestEquipServerRpc((byte)utilityType); }
            else ServerEquip(utilityType);
        }

        public void RequestThrow()
        {
            if (!IsOwner) return;
            RequestThrowServerRpc();
        }

        public void SetEquipped(bool v) { _hasEquippedUtility = v; }

        // Helper: find Hand Mount if not wired in inspector
        private Transform ResolveHandMount()
        {
            if (_viewLocal) return _viewLocal;

            // Try sockets if available
            if (sockets && sockets.handMount) { _viewLocal = sockets.handMount; return _viewLocal; }

            // Fallback by name (safe, no throws)
            var root = transform.root;
            if (root)
            {
                var t = root.Find("Hand Mount");
                if (t) { _viewLocal = t; return _viewLocal; }
            }
            return null;
        }

        // ====== Server logic ======
        [ServerRpc] void RequestEquipServerRpc(byte utility) { ServerEquip((Game.Net.UtilityType)utility); }

        void ServerEquip(Game.Net.UtilityType t)
        {
            Debug.Log($"[Utility][ServerEquip] owner={OwnerClientId} type={t}");

            _netUtilityType.Value = (byte)t;

            if (t == Game.Net.UtilityType.None)
            {
                Debug.LogWarning($"[Utility][ServerEquip] None -> disabling utility for owner={OwnerClientId}");
                ammoCount.Value = 0;
                equippedWeaponName.Value = "";
                _hasEquippedUtility = false;
                return;
            }

            ammoCount.Value = 2; // Default 2 per utility
            equippedWeaponName.Value = t.ToString();
            _hasEquippedUtility = true;

            Debug.Log($"[Utility][ServerEquip] set ammo={ammoCount.Value} name={equippedWeaponName.Value} -> rebuild local views");
            RebuildLocalViewClientRpc();
        }

        // ====== Client visuals ======
        [ClientRpc] void RebuildLocalViewClientRpc()
        {
            // Only the owner actually needs to rebuild the local first-person/hand view
            if (!IsOwner) return;
            RebuildLocalViewImmediate();
        }

        public void RebuildLocalViewImmediate()
        {
            if (_lastRebuildFrame == Time.frameCount) return; // de-dupe same-frame calls
            _lastRebuildFrame = Time.frameCount;

            Debug.Log($"[Utility][RebuildLocalViewImmediate] owner={OwnerClientId} hasEquipped={_hasEquippedUtility} netType={(Game.Net.UtilityType)_netUtilityType.Value} goActive={gameObject.activeSelf} frame={Time.frameCount}");

            // 1) If not equipped, ensure we're hidden and bail
            if (!_hasEquippedUtility)
            {
                if (gameObject.activeSelf) gameObject.SetActive(false);  // stop goActive=True while not equipped
                Debug.LogWarning($"[Utility] Abort: _hasEquippedUtility=false (parent={(transform.parent ? transform.parent.name : "<null>")}, children={transform.childCount}, frame={Time.frameCount})");
                return;
            }

            // 2) We are equipped: ensure we have a mount and snap to it
            var mount = ResolveHandMount();
            if (!mount)
            {
                Debug.LogWarning("[Utility] No Hand Mount found; keeping view inactive.");
                gameObject.SetActive(false);
                return;
            }

            if (transform.parent != mount)
            {
                transform.SetParent(mount, worldPositionStays: false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            }

            // 3) Now safe to show locally
            if (!gameObject.activeSelf) gameObject.SetActive(true);
        }

        [ServerRpc] void RequestThrowServerRpc(ServerRpcParams p = default)
        {
            if (!_hasEquippedUtility) return;
            if (ammoCount.Value <= 0) return;

            ammoCount.Value--;

            var utilityType = (Game.Net.UtilityType)_netUtilityType.Value;
            GameObject prefab = utilityType switch
            {
                Game.Net.UtilityType.Grenade => fragGrenadePrefab,
                Game.Net.UtilityType.Smoke => smokePrefab,
                Game.Net.UtilityType.Stun => stunPrefab,
                _ => null
            };

            if (!prefab)
            {
                Debug.LogWarning($"[WeaponUtility] No prefab for {utilityType}");
                return;
            }

            // Spawn throwable
            var origin = transform.position + Vector3.up * 1.5f; // Shoulder height
            var direction = GetThrowDirection();

            var go = Instantiate(prefab, origin, Quaternion.identity);
            var no = go.GetComponent<NetworkObject>();
            if (no) no.Spawn(true);

            var rb = go.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.AddForce(direction * throwForce, ForceMode.Impulse);
            }

            // Configure throwable if it has special script
            var throwable = go.GetComponent<ThrowableProjectile>();
            if (throwable)
            {
                var owner = _player ? _player : GetComponent<Game.Net.PlayerNetwork>();
                var ownerTeam = owner ? owner.GetTeam() : Game.Net.TeamId.A;
                throwable.ConfigureServer(OwnerClientId, ownerTeam, owner);
            }
        }

        Vector3 GetAimDir()
        {
            // Horizontal forward of the player
            var fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            return fwd.normalized;
        }

        Vector3 GetThrowDirection()
        {
            // Add upward angle to throw direction
            var horizontal = GetAimDir();
            var angleRad = throwAngle * Mathf.Deg2Rad;
            var direction = horizontal + Vector3.up * Mathf.Tan(angleRad);
            return direction.normalized;
        }
    }
}
