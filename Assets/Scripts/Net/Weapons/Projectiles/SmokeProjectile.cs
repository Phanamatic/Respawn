using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Non-damaging smoke. Spawns a local VFX cloud on all clients.
    public sealed class SmokeProjectile : ThrowableProjectile
    {
        [Header("Smoke")]
        [SerializeField] float smokeDuration = 8f;
        [SerializeField] GameObject smokeVfxPrefab;

        protected override void Explode()
        {
            // No damage; just tell clients to render smoke.
            ShowSmokeClientRpc();
        }

        [ClientRpc]
        private void ShowSmokeClientRpc()
        {
            if (!smokeVfxPrefab) return;
            var go = Object.Instantiate(smokeVfxPrefab, transform.position, Quaternion.identity);
            Object.Destroy(go, Mathf.Max(0.5f, smokeDuration));
        }
        // Brief dev comment: Renamed to a non-override RPC; ILPP-compliant.
    }
}
// Brief dev comment: visual concealment only; gameplay LOS integration can be added later if needed.
