using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-driven horizontal projectile.
    public sealed class BulletProjectile : NetworkBehaviour
    {
        public float speed;
        public float lifetime;
        public float damage;

        float _alive;

        public override void OnNetworkSpawn()
        {
            if (!IsServer) enabled = false; // server moves it
        }

        void Update()
        {
            if (!IsServer) return;

            // Horizontal-only move (XZ). Ignore any Y in forward.
            var fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            fwd.Normalize();

            transform.position += fwd * speed * Time.deltaTime;

            _alive += Time.deltaTime;
            if (_alive >= lifetime) { Despawn(); }
        }

        void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            // TODO: apply damage to IDamageable. For now just despawn on first hit with non-owner.
            if (other.attachedRigidbody && other.attachedRigidbody.gameObject == this.gameObject) return;
            Despawn();
        }

        void Despawn()
        {
            if (IsSpawned) NetworkObject.Despawn();
        }
    }
}