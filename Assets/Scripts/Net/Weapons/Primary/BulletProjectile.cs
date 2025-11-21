using Unity.Netcode;
using UnityEngine;
using Game.Net;

namespace Game.Net.Weapons
{
    /// Server-driven horizontal projectile.
    [RequireComponent(typeof(Collider))]
    public sealed class BulletProjectile : NetworkBehaviour
    {
        public float speed;
        public float lifetime;
        public float damage;

        // Server-authoritative replication (no prefab NetworkTransform needed)
        private readonly NetworkVariable<Vector3> _netPos = new(writePerm: NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<Quaternion> _netRot = new(writePerm: NetworkVariableWritePermission.Server);

        float _alive;
        float _spawnY;
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

            _spawnY = transform.position.y; // remember the plane we spawned on

            // Ensure trigger collider & kinematic rigidbody for reliable hits.
            var col = GetComponent<Collider>();
            if (col)
            {
                col.enabled = true;
                col.isTrigger = true;
            }

            var rb = GetComponent<Rigidbody>();
            if (!rb) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            // Speculative continuous helps avoid tunnelling even if something moves into us.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Push current transform into NVs for late-joining clients
            if (IsServer)
            {
                _netPos.Value = transform.position;
                _netRot.Value = transform.rotation;
            }

            Debug.Log($"[Weapons] Projectile spawn netId={(NetworkObject?NetworkObjectId:0)} srv={IsServer} speed={speed} damage={damage} lifetime={lifetime}");
        }

        void Update()
        {
            if (IsServer)
            {
                // Horizontal forward vector (XZ-only)
                Vector3 start = transform.position;
                var fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
                fwd.Normalize();

                // Proposed end position this frame
                Vector3 end = start + fwd * speed * Time.deltaTime;
                end.y = _spawnY; // hard-lock to spawn plane

                Vector3 stepDir = end - start;
                float stepDistance = stepDir.magnitude;

                if (!_hasImpacted && stepDistance > 0.0001f)
                {
                    stepDir /= stepDistance;

                    // Sweep along the path this frame so we can't tunnel through thin colliders.
                    if (Physics.Raycast(
                            start,
                            stepDir,
                            out var hit,
                            stepDistance,
                            s_validCollisionLayers,
                            QueryTriggerInteraction.Collide))
                    {
                        // Move to impact point first for cleaner visuals.
                        transform.position = hit.point;

                        // Resolve hit; if consumed (non-owner / non-ignored), we despawn here.
                        if (ProcessHit(hit.collider))
                        {
                            _netPos.Value = transform.position;
                            _netRot.Value = transform.rotation;
                            return;
                        }

                        // If the hit was ignored (e.g. owner collider), fall through to full step.
                    }
                }

                // No blocking hit this frame; advance to full end position.
                transform.position = end;

                // Replicate to clients
                _netPos.Value = transform.position;
                _netRot.Value = transform.rotation;

                _alive += Time.deltaTime;
                if (_alive >= lifetime)
                {
                    Debug.Log($"[Weapons] Projectile despawn (lifetime) t={_alive:0.00}");
                    Despawn();
                }
            }
            else
            {
                // Client: follow server
                transform.SetPositionAndRotation(_netPos.Value, _netRot.Value);
            }
        }

        /// <summary>
        /// Shared collision handler for trigger + swept ray hits.
        /// Returns true if the projectile was consumed (despawned) by the hit.
        /// </summary>
        bool ProcessHit(Collider other)
        {
            if (!IsServer || _hasImpacted) return false;
            if (!other) return false;

            // Ignore our own rigidbody/collider if ever hit by ray/trigger.
            if (other.attachedRigidbody && other.attachedRigidbody.gameObject == this.gameObject)
                return false;

            // Only react to configured layers (Player/Occluder/OccluderExtra).
            int otherLayer = other.gameObject.layer;
            if ((s_validCollisionLayers.value & (1 << otherLayer)) == 0)
            {
                // Ignore collision with this layer (e.g. Ground, UI, VFX, etc.)
                return false;
            }

            var target = other.GetComponentInParent<Game.Net.PlayerNetwork>();
            if (target)
            {
                // Hard ignore the shooter, including odd cases with extra colliders.
                if ((_owner && target == _owner) || target.OwnerClientId == _ownerClientId)
                    return false;

                _hasImpacted = true;

                bool friendly = _owner && target.GetTeam() == _ownerTeam;
                Debug.Log($"[Weapons] Projectile hit player victimCid={target.OwnerClientId} attackerCid={_ownerClientId} friendly={friendly} dmg={damage}");
                if (!friendly)
                {
                    target.ApplyHealthDelta(-Mathf.Abs(damage), _owner);
                }

                Despawn();
                return true;
            }

            // Non-player collider on a valid layer (e.g. Occluder / OccluderExtra).
            _hasImpacted = true;
            Debug.Log($"[Weapons] Projectile hit non-player collider={other.name} layer={LayerMask.LayerToName(otherLayer)}");
            Despawn();
            return true;
        }

        void OnTriggerEnter(Collider other)
        {
            // Trigger path is now just a backup; primary hit resolution is the swept ray in Update().
            ProcessHit(other);
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