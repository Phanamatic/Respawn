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

        /// <summary>Aligns the weapon so that the local 'grip' coincides with target (hand mount).</summary>
        public void SnapGripTo(Transform target)
        {
            if (!target) return;
            if (!grip)
            {
                transform.SetPositionAndRotation(target.position, target.rotation);
                return;
            }

            // Solve: R' = Trot * inv(grip.localRot), P' = Tpos - R' * grip.localPos
            var rPrime = target.rotation * Quaternion.Inverse(grip.localRotation);
            var pPrime = target.position - (rPrime * grip.localPosition);
            transform.SetPositionAndRotation(pPrime, rPrime);
        }

        Transform GetAimPivot()
        {
            if (tip) return tip;
            if (muzzle) return muzzle;
            return transform;
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