using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-authoritative melee weapon controller living on the player.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class WeaponMeleeController : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] PlayerWeaponSockets sockets;

        [Header("Melee Settings")]
        [SerializeField] float damage = 50f;
        [SerializeField] float range = 2.5f;
        [SerializeField] float swingCooldown = 0.8f;
        [SerializeField] LayerMask hitMask = -1; // All layers by default

        [Header("UI Replication")]
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        float _swingCooldownRemain;
        Game.Net.PlayerNetwork _player;
        bool _hasEquippedMelee;

        // Local-only visuals
        WeaponView _view;
        [SerializeField] GameObject knifeViewPrefab;
        [SerializeField] private Transform _viewLocal;        // Hand Mount
        private int _lastRebuildFrame = -1;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
            if (!sockets) sockets = GetComponent<PlayerWeaponSockets>(); // runtime fallback
        }

        // ====== API ======
        public void Equip()
        {
            if (!IsServer) { RequestEquipServerRpc(); }
            else ServerEquip();
        }

        /// <summary>Show or hide the local weapon view. Called when active slot changes.</summary>
        public void SetVisible(bool visible)
        {
            if (_view && _view.gameObject)
            {
                _view.gameObject.SetActive(visible);
            }
        }

        public void RequestSwing()
        {
            if (!IsOwner) return;
            RequestSwingServerRpc();
        }

        public void SetEquipped(bool v) { _hasEquippedMelee = v; }

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
        [ServerRpc] void RequestEquipServerRpc() { ServerEquip(); }

        void ServerEquip()
        {
            Debug.Log($"[Melee][ServerEquip] owner={OwnerClientId} enabling Knife");
            equippedWeaponName.Value = "Knife";
            _swingCooldownRemain = 0f;
            _hasEquippedMelee = true;

            Debug.Log("[Melee][ServerEquip] -> rebuild local views");
            RebuildLocalViewClientRpc();
        }

        [ServerRpc] void RequestSwingServerRpc(ServerRpcParams p = default)
        {
            if (!_hasEquippedMelee) return;
            if (_swingCooldownRemain > 0f) return;

            _swingCooldownRemain = swingCooldown;

            // Melee raycast from player forward
            var origin = transform.position + Vector3.up * 1.0f; // Chest height
            var direction = GetAimDir();

            if (Physics.Raycast(origin, direction, out RaycastHit hit, range, hitMask))
            {
                var target = hit.collider.GetComponentInParent<Game.Net.PlayerNetwork>();
                if (target)
                {
                    // Don't hit yourself
                    if (target == _player || target.OwnerClientId == OwnerClientId) return;

                    // Apply damage
                    var ownerTeam = _player ? _player.GetTeam() : Game.Net.TeamId.A;
                    var targetTeam = target.GetTeam();

                    // Only damage enemies
                    if (ownerTeam != targetTeam)
                    {
                        target.ApplyHealthDelta(-Mathf.Abs(damage), _player);
                    }
                }
            }

            // Play swing animation on all clients
            PlaySwingClientRpc();
        }

        void Update()
        {
            if (!IsServer) return;

            if (_swingCooldownRemain > 0f)
                _swingCooldownRemain -= Time.deltaTime;
        }

        Vector3 GetAimDir()
        {
            // Horizontal forward of the player
            var fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            return fwd.normalized;
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

            Debug.Log($"[Melee][RebuildLocalViewImmediate] owner={OwnerClientId} hasEquipped={_hasEquippedMelee} goActive={gameObject.activeSelf} frame={Time.frameCount}");

            // 1) If not equipped, ensure we're hidden and bail
            if (!_hasEquippedMelee)
            {
                if (gameObject.activeSelf) gameObject.SetActive(false);  // stop goActive=True while not equipped
                Debug.LogWarning($"[Melee] Abort: _hasEquippedMelee=false (viewLocal={(_viewLocal ? _viewLocal.name : "null")}, parent={(transform.parent ? transform.parent.name : "<null>")}, frame={Time.frameCount})");
                return;
            }

            // 2) We are equipped: ensure we have a mount and snap to it
            var mount = ResolveHandMount();
            if (!mount)
            {
                Debug.LogWarning("[Melee] No Hand Mount found; keeping view inactive.");
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

        [ClientRpc] void PlaySwingClientRpc()
        {
            // Play swing animation or sound
            if (_view)
            {
                // Trigger animation if available
                var animator = _view.GetComponent<Animator>();
                if (animator)
                {
                    animator.SetTrigger("Swing");
                }
            }
        }
    }
}
