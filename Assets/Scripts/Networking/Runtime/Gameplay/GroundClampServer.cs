// Assets/Scripts/Networking/Runtime/Gameplay/GroundClampServer.cs
// Server-only guard: never let the player go below the Ground layer. Also provides a spawn snap helper.
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    [DefaultExecutionOrder(-2000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GroundClampServer : NetworkBehaviour
    {
        [Header("Ground")]
        [SerializeField] LayerMask groundMask;           // set to "Ground" in Inspector
        [SerializeField, Min(0.001f)] float skin = 0.02f;
        [SerializeField, Min(0.1f)] float probeUp = 2f;
        [SerializeField, Min(0.1f)] float probeDown = 6f;

        Rigidbody _rb;
        CapsuleCollider _capsule;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            if (groundMask.value == 0)
            {
                int g = LayerMask.NameToLayer("Ground");
                if (g >= 0) groundMask = 1 << g;
            }
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) { enabled = false; return; }
            if (_rb)
            {
                _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                _rb.interpolation = RigidbodyInterpolation.None;
                _rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
            ClampImmediate();
        }

        void FixedUpdate()
        {
            ClampImmediate();
        }

        private void ClampImmediate()
        {
            // Ray from above the head down through the feet.
            Vector3 pos = transform.position;
            Vector3 start = pos + Vector3.up * probeUp;

            if (Physics.Raycast(start, Vector3.down, out var hit, probeUp + probeDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                float lift = ComputeLift(_capsule, skin);
                float minY = hit.point.y + lift;

                if (pos.y < minY)
                {
                    pos.y = minY;
                    transform.position = pos;

                    if (_rb)
                    {
                        var v = _rb.linearVelocity;
                        if (v.y < 0f) { v.y = 0f; _rb.linearVelocity = v; }
                    }
                }
            }
        }

        static float ComputeLift(CapsuleCollider capsule, float fallback)
        {
            if (!capsule) return fallback;
            // keep capsule bottom just above the ground plane
            return capsule.height * 0.5f + fallback;
        }

        // Helper for spawn-time snap.
        public static void SnapToGround(Transform t, LayerMask groundMask, float skin, CapsuleCollider capsule, float up = 10f, float down = 50f)
        {
            Vector3 point = t.position;
            Vector3 start = point + Vector3.up * up;
            if (Physics.Raycast(start, Vector3.down, out var hit, up + down, groundMask, QueryTriggerInteraction.Ignore))
            {
                float lift = ComputeLift(capsule, skin);
                point.y = hit.point.y + lift;
                t.position = point;
            }
        }

        // Find nearest clear position on the Ground layer near 'desired'.
        // If directly above ground but blocked by non-ground geometry (e.g., top of a box),
        // search radially on XZ for the first ground position where the capsule does not intersect.
        public static bool TryFindNearestClearGround(Vector3 desired, out Vector3 result,
                                                     LayerMask groundMask, CapsuleCollider capsule,
                                                     float skin = 0.02f, float up = 10f, float down = 50f,
                                                     float maxRadius = 3.5f, int rings = 6, int raysPerRing = 24)
        {
            result = desired;
            // First try the desired point
            if (TrySnapAndCheck(desired, out result, groundMask, capsule, skin, up, down))
                return true;

            // Radial search
            float stepR = Mathf.Max(0.25f, maxRadius / Mathf.Max(1, rings));
            for (int rIndex = 1; rIndex <= rings; rIndex++)
            {
                float r = rIndex * stepR;
                for (int i = 0; i < raysPerRing; i++)
                {
                    float a = (i / (float)raysPerRing) * Mathf.PI * 2f;
                    Vector3 candidate = desired + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (TrySnapAndCheck(candidate, out result, groundMask, capsule, skin, up, down))
                        return true;
                }
            }

            // As a fallback, return the last snapped position even if intersecting was never clear
            return false;
        }

        static bool TrySnapAndCheck(Vector3 pos, out Vector3 snapped, LayerMask groundMask, CapsuleCollider capsule, float skin, float up, float down)
        {
            snapped = pos;
            // Snap vertically to ground under 'pos'
            Vector3 start = pos + Vector3.up * up;
            if (!Physics.Raycast(start, Vector3.down, out var hit, up + down, groundMask, QueryTriggerInteraction.Ignore))
                return false;

            float lift = ComputeLift(capsule, skin);
            snapped.y = hit.point.y + lift;

            // Capsule overlap test at 'snapped' against all non-trigger colliders except Ground
            if (!capsule) return true;

            float radius = Mathf.Max(0.01f, capsule.radius - skin);
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            Vector3 center = snapped + capsule.center;
            Vector3 a = center + Vector3.up * half;
            Vector3 b = center - Vector3.up * half;

            // Ignore triggers
            Collider[] hits = Physics.OverlapCapsule(a, b, radius, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (!h || h.isTrigger) continue;
                // Allow Ground itself
                if (((1 << h.gameObject.layer) & groundMask.value) != 0) continue;
                // Allow own collider
                if (h.transform.IsChildOf(capsule.transform)) continue;

                return false; // blocked
            }
            return true;
        }
    }
}