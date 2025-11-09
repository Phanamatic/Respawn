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
            RebuildLocalViewImmediate();
        }

        public void RebuildLocalViewImmediate()
        {
            if (!IsOwner) return;

            if (_view) Destroy(_view.gameObject);
            _view = null;

            Debug.Log($"[Utility][RebuildLocalViewImmediate] owner={OwnerClientId} hasEquipped={_hasEquippedUtility} netType={(Game.Net.UtilityType)_netUtilityType.Value}");

            if (!_hasEquippedUtility)
            {
                Debug.LogWarning("[Utility] Abort: _hasEquippedUtility=false");
                return;
            }

            var utilityType = (Game.Net.UtilityType)_netUtilityType.Value;
            if (utilityType == Game.Net.UtilityType.None)
            {
                Debug.LogWarning("[Utility] Abort: utilityType=None");
                return;
            }

            if (!sockets)
            {
                Debug.LogWarning("[Utility] Abort: PlayerWeaponSockets missing on player. Cannot attach Utility WeaponView.");
                return;
            }
            if (!sockets.handMount)
            {
                Debug.LogWarning("[Utility] Abort: sockets.handMount not assigned. Cannot attach Utility WeaponView.");
                return;
            }

            GameObject viewPrefab = utilityType switch
            {
                Game.Net.UtilityType.Grenade => grenadeViewPrefab,
                Game.Net.UtilityType.Smoke   => smokeViewPrefab,
                Game.Net.UtilityType.Stun    => stunViewPrefab,
                _ => null
            };

            if (!viewPrefab)
            {
                Debug.LogWarning($"[Utility] Abort: No view prefab for {utilityType}. Assign in inspector.");
                return;
            }

            // Ensure only one weapon view exists under the Hand Mount
            int preChildren = sockets.handMount.childCount;
            for (int i = preChildren - 1; i >= 0; i--) Destroy(sockets.handMount.GetChild(i).gameObject);
            Debug.Log($"[Utility] Cleared handMount children: {preChildren} -> 0 (mount={sockets.handMount.name})");

            var go = Instantiate(viewPrefab);
            go.name = $"{utilityType}_View(Local)";
            _view = go.GetComponent<WeaponView>();

            var t = go.transform;

            // Bind Grip → Hand Mount
            t.SetParent(sockets.handMount, false);
            if (_view)
            {
                Debug.Log($"[Utility] Snapping view '{go.name}' grip={(bool)_view.grip} to mount={sockets.handMount.name}");
                _view.SnapGripTo(sockets.handMount);
            }
            else
            {
                Debug.LogWarning("[Utility] View prefab missing WeaponView component; falling back to raw align.");
                t.position = sockets.handMount.position; t.rotation = sockets.handMount.rotation;
            }

            // Default facing toward Front point (uses tip if present)
            if (_view && sockets.front)
            {
                Debug.Log($"[Utility] SnapAimTo front='{sockets.front.name}' using tip={(bool)_view.tip}");
                _view.SnapAimTo(sockets.front);
            }

            if (_view && sockets.equipStart && sockets.front)
            {
                Debug.Log($"[Utility] PlayEquipAnimation from='{sockets.equipStart.name}' to='{sockets.front.name}'");
                StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
            }
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
