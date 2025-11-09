using UnityEngine;
using Unity.Netcode;

namespace Game.Net.Weapons
{
    /// Damage grenade – inherits base damage/fuse behavior.
    public sealed class FragGrenadeProjectile : ThrowableProjectile
    {
        [Header("FX")]
        [SerializeField] GameObject explosionVfxPrefab;

        protected override void Explode()
        {
            base.Explode(); // applies distance falloff damage (server)
            // Trigger client-side VFX via a dedicated, non-virtual RPC
            FragVfxClientRpc();
        }

        // NOTE: replace the override with a standalone non-virtual ClientRpc
        [ClientRpc]
        private void FragVfxClientRpc()
        {
            if (explosionVfxPrefab)
            {
                var vfx = Instantiate(explosionVfxPrefab, transform.position, Quaternion.identity);
                Destroy(vfx, 5f);
            }
        }
        // Brief dev comment: Avoids overriding RPCs (not allowed). Keeps VFX in a dedicated ClientRpc.
    }
}
// Brief dev comment: keeps damage logic in base; adds simple VFX hook.
