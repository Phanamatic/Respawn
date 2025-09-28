using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    [DefaultExecutionOrder(-50)]
    public sealed class NetworkPrefabRegistry : MonoBehaviour
    {
        [Tooltip("All NetworkObject prefabs used at runtime.")]
        [SerializeField] private List<NetworkObject> prefabs = new();

        private void OnEnable()
        {
            StartCoroutine(RegisterWhenReady());
        }

        private IEnumerator RegisterWhenReady()
        {
            // Wait until a NetworkManager exists
            while (NetworkManager.Singleton == null) yield return null;

            // MPS can swap NetworkConfig during startup; wait one frame
            yield return null;

            var nm = NetworkManager.Singleton;
            if (!nm) yield break;

            Apply(nm);
            nm.OnServerStarted += () => Apply(nm);
        }

        private void Apply(NetworkManager nm)
        {
            if (!nm || nm.NetworkConfig == null) return;
            foreach (var no in prefabs)
            {
                if (!no) continue;
                var go = no.gameObject;
                if (!IsRegistered(nm, go))
                    nm.AddNetworkPrefab(go);
            }
#if UNITY_EDITOR
            Debug.Log("[NetworkPrefabRegistry] Prefabs registered.");
#endif
        }

        private static bool IsRegistered(NetworkManager nm, GameObject go)
        {
            var list = nm.NetworkConfig.Prefabs;
            if (list == null) return false;
            foreach (var e in list.Prefabs)
                if (e != null && e.Prefab == go) return true;
            return false;
        }
    }
}
