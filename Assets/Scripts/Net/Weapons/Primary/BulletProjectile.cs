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

        // Collision filter: only collide with Player, Occluder, and OccluderExtra layers
        static LayerMask s_validCollisionLayers;
        static bool s_layerMaskInitialized;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Initialize collision layer mask once
            if (!s_layerMaskInitialized)
            {
                s_validCollisionLayers = LayerMask.GetMask("Player", "Occluder", "OccluderExtra");
                s_layerMaskInitialized = true;
                Debug.Log($"[Weapons] Projectile collision layers configured: Player, Occluder, OccluderExtra (mask={s_validCollisionLayers.value})");
            }

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

            Debug.Log($"[Weapons] Projectile spawn netId={(NetworkObject?NetworkObjectId:0)} srv={IsServer} speed={speed} damage={damage} lifetime={lifetime}");
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
            if (_alive >= lifetime) { Debug.Log($"[Weapons] Projectile despawn (lifetime) t={_alive:0.00}"); Despawn(); }
        }

        void OnTriggerEnter(Collider other)
        {
            if (!IsServer || _hasImpacted) return;
            if (!other) return;
            if (other.attachedRigidbody && other.attachedRigidbody.gameObject == this.gameObject) return;

            // CRITICAL: Only collide with Player, Occluder, and OccluderExtra layers
            int otherLayer = other.gameObject.layer;
            if ((s_validCollisionLayers.value & (1 << otherLayer)) == 0)
            {
                // Ignore collision with this layer (e.g., Ground, UI, etc.)
                return;
            }

            var target = other.GetComponentInParent<Game.Net.PlayerNetwork>();
            if (target)
            {
                if ((_owner && target == _owner) || target.OwnerClientId == _ownerClientId) return; // ignore shooter collider edge cases

                _hasImpacted = true;

                bool friendly = _owner && target.GetTeam() == _ownerTeam;
                Debug.Log($"[Weapons] Projectile hit player victimCid={target.OwnerClientId} attackerCid={_ownerClientId} friendly={friendly} dmg={damage}");
                if (!friendly)
                {
                    target.ApplyHealthDelta(-Mathf.Abs(damage), _owner);
                }

                Despawn();
                return;
            }

            _hasImpacted = true;
            Debug.Log($"[Weapons] Projectile hit non-player collider={other.name} layer={LayerMask.LayerToName(otherLayer)}");
            Despawn();
        }

        void Despawn()
        {
            if (IsSpawned) NetworkObject.Despawn();
        }

        const float kSpeedMultiplier = 10f;
        const float kMinimumLifetime = 10f;

        public void ConfigureServer(float speedValue, float lifetimeSeconds, float damageValue, ulong ownerClientId, TeamId ownerTeam, Game.Net.PlayerNetwork owner)
        {
            speed = speedValue * kSpeedMultiplier;
            lifetime = Mathf.Max(kMinimumLifetime, lifetimeSeconds);
            damage = damageValue;
            _owner = owner;
            _ownerTeam = ownerTeam;
            _ownerClientId = ownerClientId;
            _alive = 0f;
            _hasImpacted = false;
            IgnoreOwnerColliders(owner);
            Debug.Log($"[Weapons] Projectile configured ownerCid={_ownerClientId} team={_ownerTeam} speed={speed} dmg={damage} life={lifetime}");
        }

        void IgnoreOwnerColliders(Game.Net.PlayerNetwork owner)
        {
            if (!owner) return;

            var projectileColliders = GetComponentsInChildren<Collider>();
            if (projectileColliders == null || projectileColliders.Length == 0) return;

            var ownerColliders = owner.GetComponentsInChildren<Collider>(true);
            if (ownerColliders == null || ownerColliders.Length == 0) return;

            for (int i = 0; i < projectileColliders.Length; i++)
            {
                var projCol = projectileColliders[i];
                if (!projCol) continue;

                for (int j = 0; j < ownerColliders.Length; j++)
                {
                    var ownerCol = ownerColliders[j];
                    if (!ownerCol) continue;
                    Physics.IgnoreCollision(projCol, ownerCol, true);
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (trail) trail.emitting = false;
            Debug.Log($"[Weapons] Projectile despawn netId={(NetworkObject?NetworkObjectId:0)} impacted={_hasImpacted}");
            _owner = null;
            _hasImpacted = false;
            _alive = 0f;
        }
    }
}