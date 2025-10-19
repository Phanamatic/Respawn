using System.Collections;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Visual gun representation on each client. Not networked.
    public sealed class WeaponView : MonoBehaviour
    {
        [Header("Attachment Points")]
        public Transform grip;   // attach to player's HandMount
        public Transform muzzle; // points to sockets

        public IEnumerator PlayEquipAnimation(Transform targetStart, Transform targetFront, float secs = 0.25f)
        {
            if (!this || !gameObject || !enabled || !muzzle) yield break;

            // Step 1
            if (targetStart && this && muzzle)
            {
                var a = muzzle ? muzzle.position : transform.position;
                var b = targetStart ? targetStart.position : a;
                Vector3 dir = b - a; dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Step 2
            float t = 0f;
            while (t < secs)
            {
                if (!this || !gameObject || !enabled || !muzzle) yield break;
                t += Time.deltaTime;
                if (targetFront)
                {
                    var a = muzzle ? muzzle.position : transform.position;
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
// Stops MissingReferenceException when the view is destroyed mid-animation.
    }
}