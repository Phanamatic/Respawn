// Assets/Scripts/Networking/Runtime/Gameplay/PlayerVisualAnchor.cs
using System.Linq;
using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    /// <summary>
    /// Client-only anchor for cinematics. Never reparent the NetworkObject root on clients.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerVisualAnchor : MonoBehaviour
    {
        [Tooltip("Purely visual root. No NetworkObject or Rigidbody. If empty, auto-find.")]
        [SerializeField] private Transform visualRoot;

        private Collider[] _cachedColliders;
        private bool _collidersCached;

        /// <summary>Safe to reparent on clients. Null if only the NetworkObject root exists.</summary>
        public Transform SafeVisualRoot
        {
            get
            {
                if (!visualRoot) TryAutoBind();
                if (!visualRoot) return null;
                var nob = GetComponent<NetworkObject>();
                if (nob && visualRoot == transform) return null;
                if (visualRoot.GetComponent<NetworkObject>()) return null;
                return visualRoot;
            }
        }

        void Awake()
        {
            if (!visualRoot) TryAutoBind();
        }

        /// <summary>Find a safe visual root with renderers and no NetworkObject/Rigidbody.</summary>
        private void TryAutoBind()
        {
            var named = transform.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t != transform && (t.name.Contains("Model") || t.name.Contains("Visual")));
            if (named && IsSafe(named))
            {
                visualRoot = named;
                return;
            }

            foreach (var t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t == transform) continue;
                if (!t.GetComponentInChildren<Renderer>(true)) continue;
                if (IsSafe(t))
                {
                    visualRoot = t;
                    return;
                }
            }
        }

        private static bool IsSafe(Transform t)
        {
            if (!t) return false;
            if (t.GetComponent<NetworkObject>()) return false;
            if (t.GetComponent<Rigidbody>()) return false;
            return true;
        }

        public void AttachTo(Transform parent, Vector3 localPos, Quaternion localRot, bool disableColliders)
        {
            var root = SafeVisualRoot;
            if (!parent || !root) return;

            if (disableColliders) SetCollidersEnabled(false);
            root.SetParent(parent, false);
            root.localPosition = localPos;
            root.localRotation = localRot;
        }

        public void DetachToWorld(bool restoreColliders)
        {
            var root = SafeVisualRoot;
            if (!root) return;
            root.SetParent(null, true);
            if (restoreColliders) SetCollidersEnabled(true);
        }

        private void CacheColliders()
        {
            if (_collidersCached) return;
            var root = SafeVisualRoot;
            if (root)
                _cachedColliders = root.GetComponentsInChildren<Collider>(true);
            _collidersCached = true;
        }

        private void SetCollidersEnabled(bool enabled)
        {
            CacheColliders();
            if (_cachedColliders == null) return;
            for (int i = 0; i < _cachedColliders.Length; i++)
                if (_cachedColliders[i]) _cachedColliders[i].enabled = enabled;
        }
    }
}
