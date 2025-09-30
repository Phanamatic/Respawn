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
    }
}