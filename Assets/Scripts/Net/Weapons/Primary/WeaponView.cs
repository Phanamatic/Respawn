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

        void OnDestroy() { StopAllCoroutines(); } // avoid coroutine touching destroyed transforms

        /// <summary>Align the weapon so its Grip matches the Hand Mount pose exactly.</summary>
        public void SnapGripTo(Transform handMount)
        {
            if (!handMount) return;

            // If no grip, fall back to simple parenting/alignment.
            if (!grip)
            {
                transform.SetPositionAndRotation(handMount.position, handMount.rotation);
                return;
            }

            // Compute rotation delta to make grip.rotation == handMount.rotation
            var rotDelta = handMount.rotation * Quaternion.Inverse(grip.rotation);
            transform.rotation = rotDelta * transform.rotation;

            // After rotation, move root so grip.position == handMount.position
            var worldOffset = handMount.position - grip.position;
            transform.position += worldOffset;
        }

        Transform GetAimPivot()
        {
            if (tip) return tip;
            if (muzzle) return muzzle;
            return transform;
        }

        /// <summary>Immediately turns the weapon so its pivot (tip→muzzle→self) faces the target point on the horizontal plane.</summary>
        public void SnapAimTo(Transform targetFront)
        {
            if (!targetFront) return;
            var pivot = GetAimPivot();
            var a = pivot.position;
            var b = targetFront.position;
            var dir = b - a; dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
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

            var target = ray.GetPoint(t);
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