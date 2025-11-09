using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Stun grenade – disables enemy input briefly; no damage.
    public sealed class StunProjectile : ThrowableProjectile
    {
        [Header("Stun")]
        [SerializeField] float stunRadius = 6f;
        [SerializeField] float stunSeconds = 2.5f;
        [SerializeField] LayerMask hitMask = ~0;
        [SerializeField] GameObject stunVfxPrefab;

        protected override void Explode()
        {
            // Server: find players in radius and individually stun enemy players.
            var cols = Physics.OverlapSphere(transform.position, stunRadius, hitMask, QueryTriggerInteraction.Collide);
            foreach (var col in cols)
            {
                var target = col.GetComponentInParent<Game.Net.PlayerNetwork>();
                if (!target || target == _owner) continue;
                // Skip friendlies
                if (_owner && target.GetTeam() == _ownerTeam) continue;

                // Target this client's local player to pause input briefly.
                var rpc = Game.Net.PlayerNetwork.TargetClientParams(target.OwnerClientId);
                ApplyStunClientRpc(stunSeconds, rpc);
            }

            // Non-virtual RPC for client-side VFX
            ShowStunVfxClientRpc();
        }

        [ClientRpc]
        void ApplyStunClientRpc(float seconds, ClientRpcParams rpcParams = default)
        {
            var nm = NetworkManager.Singleton;
            var local = nm ? nm.LocalClient?.PlayerObject?.GetComponent<Game.Net.PlayerNetwork>() : null;
            if (local)
            {
                local.StartLocalInputPause(seconds);
            }
        }

        [ClientRpc]
        private void ShowStunVfxClientRpc()
        {
            if (stunVfxPrefab)
            {
                var vfx = Object.Instantiate(stunVfxPrefab, transform.position, Quaternion.identity);
                Object.Destroy(vfx, 5f);
            }
        }
        // Brief dev comment: Keeps ApplyStunClientRpc targeted; splits VFX into its own non-virtual RPC.
    }
}
// Brief dev comment: uses targeted ClientRpc to pause only the affected client's input.
