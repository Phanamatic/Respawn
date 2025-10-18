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
            if (!muzzle) yield break;

            // Step 1: point muzzle at EquipStart
            if (targetStart)
            {
                Vector3 dir = (targetStart.position - muzzle.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Step 2: glide to Front aim
            float t = 0f;
            while (t < secs)
            {
                t += Time.deltaTime;
                if (targetFront)
                {
                    Vector3 dir = (targetFront.position - muzzle.position);
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        var q = Quaternion.LookRotation(dir.normalized, Vector3.up);
                        transform.rotation = Quaternion.Slerp(transform.rotation, q, Mathf.Clamp01(t / secs));
                    }
                }
                yield return null;
            }
        }
    }
}