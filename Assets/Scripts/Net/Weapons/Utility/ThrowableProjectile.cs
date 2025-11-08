using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// <summary>
    /// Base class for throwable projectiles (grenade, smoke, stun).
    /// Attach to throwable prefabs. Override to implement specific behavior.
    /// </summary>
    public class ThrowableProjectile : NetworkBehaviour
    {
        [Header("Throwable Settings")]
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
