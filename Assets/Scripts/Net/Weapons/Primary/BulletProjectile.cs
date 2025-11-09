using Unity.Netcode;
using UnityEngine;
using Game.Net;

namespace Game.Net.Weapons
{
    /// <summary>
    /// Server-authoritative bullet with FixedUpdate sweep raycasting.
    /// Prevents tunneling and ensures reliable hit detection on PlayerHitbox|Occluder|ExtraOccluder layers.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public sealed class BulletProjectile : NetworkBehaviour
    {
        [Header("Bullet Properties")]
        [SerializeField] private float _speed = 38f;
        [SerializeField] private int _damage = 9;
        [SerializeField] private float _lifetime = 10f;
        
        [Header("Hit Detection")]
        [SerializeField] private LayerMask _hitMask; // PlayerHitbox|Occluder|ExtraOccluder
        [SerializeField] private float _skin = 0.05f; // Extra sweep distance
        
        [Header("Gameplay Plane")]
        [SerializeField] private float _lockY = 0f; // Set by spawner to gameplay plane Y
        
        [Header("Visual")]
        [SerializeField] private TrailRenderer trail;
        
        private Vector3 _lastPos;
        private float _life;
        private bool _impacted;
        private ulong _ownerClientId;
        private float _spawnIgnoreSelfWindow = 0.08f; // Grace period to avoid muzzle self-hits
        
        private static Material s_trailMaterial;

        
        /// <summary>
        /// Called by spawner on server immediately after Instantiate, before Spawn().
        /// </summary>
        public void ServerInit(Vector3 forward, ulong ownerClientId, float yPlane)
        {
            if (!IsServer) return;
            transform.forward = forward;
            _ownerClientId = ownerClientId;
            _lockY = yPlane;
            Debug.Log($"[Weapons] Projectile ServerInit owner={ownerClientId} dir={forward} yPlane={yPlane}");
        }

        /// <summary>
        /// Configure bullet properties on server before spawn. Called by weapon controllers.
        /// </summary>
        public void ConfigureServer(float speed, float lifetime, float damage, ulong ownerClientId, Game.Net.TeamId ownerTeam, Game.Net.PlayerNetwork owner)
        {
            if (!IsServer) return;
            _speed = speed;
            _lifetime = lifetime;
            _damage = Mathf.RoundToInt(damage);
            _ownerClientId = ownerClientId;
            _lockY = owner ? owner.transform.position.y : 0f;
            Debug.Log($"[Weapons] Projectile ConfigureServer owner={ownerClientId} team={ownerTeam} speed={speed} dmg={damage} lifetime={lifetime}");
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            Debug.Log($"[Weapons] Projectile spawn netId={NetworkObjectId} srv={IsServer} speed={_speed} dmg={_damage} lifetime={_lifetime}");
            
            _life = _lifetime;
            _lastPos = transform.position;
            
            // Lock to gameplay plane
            if (_lockY != 0f)
            {
                var pos = transform.position;
                pos.y = _lockY;
                transform.position = pos;
                _lastPos = pos;
            }


            // Setup trail renderer
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
        }

        private void FixedUpdate()
        {
            if (!IsServer || _impacted || !IsSpawned) return;

            // Lifetime check
            _life -= Time.fixedDeltaTime;
            if (_life <= 0f)
            {
                Debug.Log($"[Weapons] Projectile despawn (lifetime expired) netId={NetworkObjectId}");
                Despawn(false);
                return;
            }

            // Advance bullet and sweep for hits
            var dir = transform.forward;
            var step = _speed * Time.fixedDeltaTime;
            var start = transform.position;
            var end = start + dir * step;
            
            // Lock to gameplay plane
            if (_lockY != 0f)
            {
                end.y = _lockY;
            }

            var dist = (end - start).magnitude;
            
            // Raycast sweep from last position to next position (prevents tunneling)
            if (Physics.Raycast(start, dir, out var hit, dist + _skin, _hitMask, QueryTriggerInteraction.Ignore))
            {
                // Grace window: ignore owner hits for first few frames to avoid muzzle self-hits
                if (_spawnIgnoreSelfWindow > 0f)
                {
                    _spawnIgnoreSelfWindow -= Time.fixedDeltaTime;
                    
                    var pn = hit.collider.GetComponentInParent<Game.Net.PlayerNetwork>();
                    if (pn && pn.OwnerClientId == _ownerClientId)
                    {
                        // Still in grace window, ignore self hit and continue
                        transform.position = end;
                        _lastPos = transform.position;
                        return;
                    }
                }

                // Valid hit detected
                Debug.Log($"[Weapons] Projectile hit collider={hit.collider.name} point={hit.point} netId={NetworkObjectId}");

                // Try to apply damage via Health component
                var health = hit.collider.GetComponentInParent<Health>();
                if (health && health.HasServerAuthority)
                {
                    Debug.Log($"[Weapons] Applying damage={_damage} to Health on {hit.collider.name}");
                    health.ApplyDamage(_damage, _ownerClientId, hit.point);
                }

                // Notify clients to play impact FX
                PlayImpactClientRpc(hit.point, hit.normal);

                _impacted = true;
                Despawn(true);
            }
            else
            {
                // No hit, advance bullet
                transform.position = end;
                _lastPos = transform.position;
            }
        }

        [ClientRpc]
        private void PlayImpactClientRpc(Vector3 point, Vector3 normal)
        {
            // TODO: Spawn impact VFX/SFX on all clients if desired
            Debug.Log($"[Weapons] Impact FX at {point} (clientside)");
        }

        private void Despawn(bool impacted)
        {
            if (_impacted != impacted) _impacted = impacted;
            if (IsSpawned)
            {
                NetworkObject.Despawn();
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (trail) trail.emitting = false;
            Debug.Log($"[Weapons] Projectile despawn netId={NetworkObjectId} impacted={_impacted}");
        }
    }
}