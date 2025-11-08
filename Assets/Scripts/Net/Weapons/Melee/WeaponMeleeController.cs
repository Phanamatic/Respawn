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

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
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

        // ====== Server logic ======
        [ServerRpc] void RequestEquipServerRpc() { ServerEquip(); }

        void ServerEquip()
        {
            equippedWeaponName.Value = "Knife";
            _swingCooldownRemain = 0f;
            _hasEquippedMelee = true;

            // Rebuild local visuals on all clients
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
            if (_view) Destroy(_view.gameObject);
            _view = null;

            if (!_hasEquippedMelee) return;

            if (!sockets)
            {
                Debug.LogWarning("[Weapons] PlayerWeaponSockets missing on player. Cannot attach Melee WeaponView.");
                return;
            }
            if (!sockets.handMount)
            {
                Debug.LogWarning("[Weapons] sockets.handMount not assigned. Cannot attach Melee WeaponView.");
                return;
            }
            if (!knifeViewPrefab)
            {
                Debug.LogWarning("[Weapons] No Knife WeaponView prefab set. Assign knifeViewPrefab.");
                return;
            }

            var go = Instantiate(knifeViewPrefab);
            go.name = "Knife_View(Local)";
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

            if (_view && sockets.equipStart && sockets.front)
            {
                StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
            }
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
