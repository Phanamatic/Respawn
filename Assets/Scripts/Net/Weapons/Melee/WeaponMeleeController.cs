using System.Collections.Generic;
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
        [SerializeField] float arcDegrees = 110f;
        [SerializeField] float swingCooldown = 0.8f;
        [SerializeField] LayerMask hitMask = -1; // All layers by default

        [Header("UI Replication")]
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        float _swingCooldownRemain;
        Game.Net.PlayerNetwork _player;
        bool _hasEquippedMelee;
        readonly HashSet<ulong> _alreadyHit = new();

        // Local-only visuals
        WeaponView _view;
        [SerializeField] GameObject knifeViewPrefab;

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
            if (!sockets) sockets = GetComponent<PlayerWeaponSockets>(); // runtime fallback
        }

        // ====== API ======
        public void Equip()
        {
            // Owner path: mark equipped immediately so local rebuild isn't gated on server RTT.
            if (!IsServer)
            {
                _hasEquippedMelee = true;
                RequestEquipServerRpc();
            }
            else
            {
                ServerEquip();
            }
        }

// Brief dev comment: client pre-sets _hasEquippedMelee so PlayerNetwork.OnActiveSlotChanged → RebuildLocalViewImmediate() doesn't bail out with hasEquipped=false.

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
            Debug.Log($"[Melee][ServerEquip] owner={OwnerClientId} enabling Knife");
            equippedWeaponName.Value = "Knife";
            _swingCooldownRemain = 0f;
            _hasEquippedMelee = true;

            Debug.Log("[Melee][ServerEquip] -> rebuild local views");
            // Fan-out the authoritative equip flag before clients rebuild their local view.
            RebuildLocalViewClientRpc(_hasEquippedMelee);
        }

        [ServerRpc] void RequestSwingServerRpc(ServerRpcParams p = default)
        {
            if (!_hasEquippedMelee) return;
            if (_swingCooldownRemain > 0f) return;

            _swingCooldownRemain = swingCooldown;

            // Fan a short arc in front of the player and damage any enemy inside it.
            var origin = sockets && sockets.meleeSwingPivot ? sockets.meleeSwingPivot.position : transform.position + Vector3.up * 1.0f;
            var direction = GetAimDir();

            _alreadyHit.Clear();

            var cols = Physics.OverlapSphere(origin, range, hitMask, QueryTriggerInteraction.Collide);
            foreach (var col in cols)
            {
                var target = col.GetComponentInParent<Game.Net.PlayerNetwork>();
                if (!target || target == _player || target.OwnerClientId == OwnerClientId) continue;
                if (_alreadyHit.Contains(target.OwnerClientId)) continue;

                var ownerTeam = _player ? _player.GetTeam() : Game.Net.TeamId.A;
                var targetTeam = target.GetTeam();
                if (ownerTeam == targetTeam) continue;

                var toTarget = target.transform.position - origin; toTarget.y = 0f;
                if (toTarget.sqrMagnitude < 0.0001f) continue;

                float angle = Vector3.Angle(direction, toTarget);
                if (angle > arcDegrees * 0.5f) continue;

                _alreadyHit.Add(target.OwnerClientId);
                target.ApplyHealthDelta(-Mathf.Abs(damage), _player);
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
            if (sockets && sockets.meleeFront)
            {
                var dir = sockets.meleeFront.forward; dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f) return dir.normalized;
            }

            // Horizontal forward of the player
            var fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            return fwd.normalized;
        }

        // ====== Client visuals ======
        [ClientRpc] void RebuildLocalViewClientRpc(bool hasEquipped)
        {
            // Mirror server equip state locally, then rebuild against it.
            _hasEquippedMelee = hasEquipped;
            RebuildLocalViewImmediate();
        }

// Brief dev comment: RPC now carries the equip flag so non-host clients never hit the _hasEquippedMelee=false early-out during a rebuild.

        public void RebuildLocalViewImmediate()
        {
            if (_player && _player.GetActiveSlot() != Game.Net.WeaponSlot.Melee)
            {
                Debug.Log("[Melee] Skip rebuild: slot not active.");
                return;
            }

            if (_view) Destroy(_view.gameObject);
            _view = null;

            Debug.Log($"[Melee][RebuildLocalViewImmediate] owner={OwnerClientId} hasEquipped={_hasEquippedMelee} goActive={gameObject.activeInHierarchy} frame={Time.frameCount}");

            if (!_hasEquippedMelee)
            {
                Debug.LogWarning("[Melee] Abort: _hasEquippedMelee=false (viewLocal="
                    + (transform.childCount > 0 ? transform.GetChild(0).name : "<none>")
                    + ", parent=" + (transform.parent ? transform.parent.name : "<null>")
                    + ", frame=" + Time.frameCount + ")");
                return;
            }

            if (!sockets)
            {
                Debug.LogWarning("[Melee] Abort: PlayerWeaponSockets missing on player. Cannot attach Melee WeaponView.");
                return;
            }
            if (!sockets.handMount)
            {
                Debug.LogWarning("[Melee] Abort: sockets.handMount not assigned. Cannot attach Melee WeaponView.");
                return;
            }
            if (!knifeViewPrefab)
            {
                Debug.LogWarning("[Melee] Abort: No Knife WeaponView prefab set. Assign knifeViewPrefab.");
                return;
            }

            int preChildren = sockets.handMount.childCount;
            for (int i = preChildren - 1; i >= 0; i--) Destroy(sockets.handMount.GetChild(i).gameObject);
            Debug.Log($"[Melee] Cleared handMount children: {preChildren} -> 0 (mount={sockets.handMount.name})");

            var go = Instantiate(knifeViewPrefab);
            go.name = "Knife_View(Local)";
            _view = go.GetComponent<WeaponView>();

            var t = go.transform;

            // Bind Grip → Hand Mount and install live anchor so it stays glued while we animate.
            t.SetParent(sockets.handMount, false);
            if (_view)
            {
                _view.SetHandMount(sockets.handMount);
                Debug.Log($"[Melee] Snapping view '{go.name}' grip={(bool)_view.grip} to mount={sockets.handMount.name}");
                _view.SnapGripTo(sockets.handMount);
            }
            else
            {
                Debug.LogWarning("[Melee] View prefab missing WeaponView component; falling back to raw align.");
                t.position = sockets.handMount.position; t.rotation = sockets.handMount.rotation;
            }

            // Default facing toward a melee-specific front (uses tip if present)
            var meleeFront = sockets.meleeFront ? sockets.meleeFront : sockets.front;
            if (_view && meleeFront)
            {
                Debug.Log($"[Melee] SnapAimTo meleeFront='{meleeFront.name}' using tip={(bool)_view.tip}");
                _view.SnapAimTo(meleeFront);
            }

            if (_view && sockets.equipStart && sockets.front)
            {
                Debug.Log($"[Melee] PlayEquipAnimation from='{sockets.equipStart.name}' to='{sockets.front.name}'");
                StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
            }
        }

        [ClientRpc] void PlaySwingClientRpc()
        {
            if (!_view) return;

            // Procedural swing on all clients; damage stays server-authoritative via RequestSwingServerRpc.
            _view.PlaySwingAnimation();

            // Optional Animator trigger for any extra VFX/sound you may wire up.
            var animator = _view.GetComponent<Animator>();
            if (animator)
            {
                animator.SetTrigger("Swing");
            }
        }
    }
}
