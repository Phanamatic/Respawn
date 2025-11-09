using System.Collections;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Visual gun representation on each client. Not networked.
    public sealed class WeaponView : MonoBehaviour
    {
        [Header("Attachment Points")]
        public Transform grip;   // snap this point to PlayerWeaponSockets.handMount
        public Transform muzzle; // used by guns
        public Transform tip;    // used by melee/utility to define the forward edge

        // Optional live anchor: if set, we keep grip.position == handMount.position every frame.
        [SerializeField] Transform _handMount;

        /// <summary>Assign the player hand mount this view should lock to.</summary>
        public void SetHandMount(Transform t) => _handMount = t;

        /// <summary>Position-only anchor: moves the whole weapon so Grip lands exactly on Hand Mount.</summary>
        void AnchorGripPosition()
        {
            if (!_handMount || !grip) return;
            Vector3 delta = _handMount.position - grip.position; // world-space correction
            if (delta.sqrMagnitude > 1e-8f) transform.position += delta;
        }

        void LateUpdate()
        {
            // Keep the hand glued even after any rotations/animations this frame.
            AnchorGripPosition();
        }

        void OnDestroy() { StopAllCoroutines(); } // avoid coroutine touching destroyed transforms

        // (other methods unchanged)

        /// <summary>Align the weapon so its Grip matches the Hand Mount pose exactly.</summary>
        public void SnapGripTo(Transform handMount)
        {
            if (!handMount)
            {
                Debug.LogWarning("[WeaponView] SnapGripTo aborted: handMount=null");
                return;
            }

            // If no grip, fall back to simple parenting/alignment.
            if (!grip)
            {
                Debug.LogWarning($"[WeaponView] No grip on '{name}'. Using raw align to mount='{handMount.name}'");
                transform.SetPositionAndRotation(handMount.position, handMount.rotation);
                return;
            }

            // Compute rotation delta to make grip.rotation == handMount.rotation
            var rotDelta = handMount.rotation * Quaternion.Inverse(grip.rotation);
            transform.rotation = rotDelta * transform.rotation;

            // After rotation, move root so grip.position == handMount.position
            var worldOffset = handMount.position - grip.position;
            transform.position += worldOffset;

            Debug.Log($"[WeaponView] Grip snapped: view='{name}' -> mount='{handMount.name}' pos={transform.position}");
            // Add parent + local TR so we can see if we're actually parented vs just teleported.
            var parentName = transform.parent ? transform.parent.name : "<null>";
            Debug.Log($"[WeaponView] Parent='{parentName}' localPos={transform.localPosition} localEuler={transform.localEulerAngles}");
        }

        Transform GetAimPivot()
        {
            if (tip) return tip;
            if (muzzle) return muzzle;
            return transform;
        }

        /// <summary>
        /// Rotate the whole weapon AROUND the grip so the vector Grip→Muzzle (projected to XZ)
        /// points at worldTarget (also projected to XZ). Returns true if applied.
        /// </summary>
        bool YawGripToTargetXZ(Vector3 worldTarget)
        {
            if (!grip || !muzzle) return false;

            var gp = grip.position;
            var mp = muzzle.position;

            // Current (barrel) and desired directions on the horizontal plane.
            var current = mp - gp; current.y = 0f;
            var desired = worldTarget - gp; desired.y = 0f;

            if (current.sqrMagnitude < 1e-6f || desired.sqrMagnitude < 1e-6f)
                return false;

            var deltaYaw = Vector3.SignedAngle(current, desired, Vector3.up);
            if (Mathf.Abs(deltaYaw) < 0.01f) return true;

            // Rotate around the grip so the hand stays locked while the muzzle swings toward target.
            transform.RotateAround(gp, Vector3.up, deltaYaw);
            return true;
        }

        /// <summary>Immediately turns the weapon so its pivot (tip→muzzle→self) faces the target point on the horizontal plane.</summary>
        public void SnapAimTo(Transform targetFront)
        {
            if (!targetFront)
            {
                Debug.LogWarning($"[WeaponView] SnapAimTo aborted: targetFront=null on '{name}'");
                return;
            }

            var pivot = GetAimPivot();
            Debug.Log($"[WeaponView] Aim pivot='{pivot.name}' tip={(bool)tip} muzzle={(bool)muzzle} -> front='{targetFront.name}'");

            // Prefer aligning the actual barrel line (Grip→Muzzle) toward target on XZ.
            if (!YawGripToTargetXZ(targetFront.position))
            {
                // Fallback: legacy pivot-based facing.
                var a = pivot.position;
                var b = targetFront.position;
                var dir = b - a; dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) return;
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
        }

        /// <summary>Aim toward the mouse on a horizontal plane (y = planeY). Returns true if aimed.</summary>
        public bool AimAtMouse(Camera cam, float planeY)
        {
#if ENABLE_INPUT_SYSTEM
            if (!cam) return false;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;

            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
            if (!plane.Raycast(ray, out var t)) return false;

            var target = ray.GetPoint(t); target.y = planeY;

            // Ensure grip is exactly on the Hand Mount before rotating to aim.
            AnchorGripPosition();

            // Primary path: align the actual barrel line (Grip→Muzzle) toward the mouse.
            if (YawGripToTargetXZ(target)) return true;

            // Fallback: pivot-facing if grip/muzzle not present.
            var pivot = GetAimPivot();
            var dir = target - pivot.position; dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return false;

            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            return true;
#else
            return false;
#endif
        }

        public IEnumerator PlayEquipAnimation(Transform targetStart, Transform targetFront, float secs = 0.25f)
        {
            if (!this || !gameObject || !enabled) yield break;

            var pivot = GetAimPivot();

            // Step 1
            if (targetStart && this && pivot)
            {
                var a = pivot.position;
                var b = targetStart ? targetStart.position : a;
                Vector3 dir = b - a; dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Step 2
            float t = 0f;
            while (t < secs)
            {
                if (!this || !gameObject || !enabled) yield break;
                t += Time.deltaTime;
                if (targetFront && pivot)
                {
                    var a = pivot.position;
                    var b = targetFront ? targetFront.position : a;
                    Vector3 dir = b - a; dir.y = 0f;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        var q = Quaternion.LookRotation(dir.normalized, Vector3.up);
                        transform.rotation = Quaternion.Slerp(transform.rotation, q, Mathf.Clamp01(t / secs));
                    }
                }
                yield return null;
            }
        }
// Brief dev comment: adds 'tip' for melee/utility aim and SnapGripTo() to mathematically align grip to hand mount.
    }
}