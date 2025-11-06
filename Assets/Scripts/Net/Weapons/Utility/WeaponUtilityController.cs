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

        [Header("Settings")]
        [SerializeField] float throwForce = 20f;
        [SerializeField] float throwAngle = 30f; // degrees upward

        Game.Net.PlayerNetwork _player;
        bool _hasEquippedUtility;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
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
            _netUtilityType.Value = (byte)t;

            if (t == Game.Net.UtilityType.None)
            {
                ammoCount.Value = 0;
                equippedWeaponName.Value = "";
                _hasEquippedUtility = false;
                return;
            }

            ammoCount.Value = 2; // Default 2 per utility
            equippedWeaponName.Value = t.ToString();
            _hasEquippedUtility = true;
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

    /// <summary>
    /// Base class for throwable projectiles (grenade, smoke, stun).
    /// Attach to throwable prefabs. Override to implement specific behavior.
    /// </summary>
    public class ThrowableProjectile : NetworkBehaviour
    {
        [SerializeField] protected float fuseTime = 3f;
        [SerializeField] protected float damageRadius = 5f;
        [SerializeField] protected float damage = 75f;

        protected float _fuseRemain;
        protected ulong _ownerClientId = ulong.MaxValue;
        protected Game.Net.TeamId _ownerTeam = Game.Net.TeamId.A;
        protected Game.Net.PlayerNetwork _owner;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                _fuseRemain = fuseTime;
            }
        }

        void Update()
        {
            if (!IsServer) return;

            _fuseRemain -= Time.deltaTime;
            if (_fuseRemain <= 0f)
            {
                Explode();
                Despawn();
            }
        }

        protected virtual void Explode()
        {
            // Find all players in radius
            var cols = Physics.OverlapSphere(transform.position, damageRadius);
            foreach (var col in cols)
            {
                var target = col.GetComponentInParent<Game.Net.PlayerNetwork>();
                if (target && target != _owner)
                {
                    // Check team
                    if (_owner && target.GetTeam() == _ownerTeam) continue;

                    // Apply damage falloff by distance
                    var dist = Vector3.Distance(transform.position, target.transform.position);
                    var falloff = 1f - Mathf.Clamp01(dist / damageRadius);
                    var finalDamage = damage * falloff;

                    target.ApplyHealthDelta(-Mathf.Abs(finalDamage), _owner);
                }
            }

            // Play explosion effect on all clients
            PlayExplosionClientRpc();
        }

        void Despawn()
        {
            if (IsSpawned) NetworkObject.Despawn();
        }

        public void ConfigureServer(ulong ownerClientId, Game.Net.TeamId ownerTeam, Game.Net.PlayerNetwork owner)
        {
            _ownerClientId = ownerClientId;
            _ownerTeam = ownerTeam;
            _owner = owner;
        }

        [ClientRpc]
        protected virtual void PlayExplosionClientRpc()
        {
            // Play explosion sound/VFX
            // Override in subclasses for specific effects
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            _owner = null;
        }
    }
}
