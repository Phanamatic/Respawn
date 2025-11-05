using Unity.Netcode;
using UnityEngine;
using Game.Net;

namespace Game.Net.Weapons
{
    /// Server-driven horizontal projectile.
    public sealed class BulletProjectile : NetworkBehaviour
    {
        public float speed;
        public float lifetime;
        public float damage;

        float _alive;
        Game.Net.PlayerNetwork _owner;
        TeamId _ownerTeam = TeamId.A;
        ulong _ownerClientId = ulong.MaxValue;
        bool _hasImpacted;
        [SerializeField] TrailRenderer trail;
        static Material s_trailMaterial;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (!trail)
                trail = GetComponentInChildren<TrailRenderer>();
            if (!trail)
            {
                trail = gameObject.AddComponent<TrailRenderer>();
                trail.time = 0.25f;
                trail.startWidth = 0.06f;
                trail.endWidth = 0f;
                trail.minVertexDistance = 0.01f;
                trail.numCornerVertices = 4;
                trail.numCapVertices = 2;
                trail.alignment = LineAlignment.View;
                if (!s_trailMaterial)
                {
                    var shader = Shader.Find("Sprites/Default");
                    if (shader)
                    {
                        s_trailMaterial = new Material(shader)
                        {
                            color = new Color(1f, 0.95f, 0.6f, 0.85f)
                        };
                    }
                }
                if (s_trailMaterial)
                    trail.material = s_trailMaterial;
                trail.startColor = new Color(1f, 0.95f, 0.6f, 0.9f);
                trail.endColor = new Color(1f, 0.95f, 0.6f, 0f);
            }
            if (trail)
            {
                trail.Clear();
                trail.emitting = true;
            }

            if (!IsServer)
                enabled = false; // server moves it
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
            if (!IsServer || _hasImpacted) return;
            if (!other) return;
            if (other.attachedRigidbody && other.attachedRigidbody.gameObject == this.gameObject) return;

            var target = other.GetComponentInParent<Game.Net.PlayerNetwork>();
            if (target)
            {
                if ((_owner && target == _owner) || target.OwnerClientId == _ownerClientId) return; // ignore shooter collider edge cases

                _hasImpacted = true;

                if (!_owner || target.GetTeam() != _ownerTeam)
                {
                    target.ApplyHealthDelta(-Mathf.Abs(damage), _owner);
                }

                Despawn();
                return;
            }

            _hasImpacted = true;
            Despawn();
        }

        void Despawn()
        {
            if (IsSpawned) NetworkObject.Despawn();
        }

        public void ConfigureServer(float speedValue, float lifetimeSeconds, float damageValue, ulong ownerClientId, TeamId ownerTeam, Game.Net.PlayerNetwork owner)
        {
            speed = speedValue;
            _ = lifetimeSeconds; // lifetime fixed to 3 seconds per design
            lifetime = 3f;
            damage = damageValue;
            _owner = owner;
            _ownerTeam = ownerTeam;
            _ownerClientId = ownerClientId;
            _alive = 0f;
            _hasImpacted = false;
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (trail) trail.emitting = false;
            _owner = null;
            _hasImpacted = false;
            _alive = 0f;
        }
    }
}