// Assets/Scripts/Networking/Runtime/Gameplay/PlayerSpawner.cs
// Server-only spawner with prefab auto-resolve + runtime registration (NGO-version-agnostic).

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.Netcode;

namespace Game.Net
{
    [DefaultExecutionOrder(-10000)]
    public sealed class PlayerSpawner : MonoBehaviour
    {
        [Header("Player Prefab (optional if registered in Network Prefabs)")]
        [SerializeField] private NetworkObject playerPrefab;

        [Header("Spawn Points (optional)")]
        [SerializeField] private List<Transform> spawnPoints = new();
        [SerializeField, Min(0f)] private float randomRadius = 6f;

        private static PlayerSpawner s_Instance;
        private readonly HashSet<ulong> _spawned = new();
        private readonly HashSet<ulong> _pending = new();

        private NetworkManager NM => NetworkManager.Singleton;

        private void OnEnable()
        {
            if (s_Instance && s_Instance != this) { enabled = false; return; }
            s_Instance = this;

            if (NM == null) { Debug.LogError("[PlayerSpawner] No NetworkManager."); return; }

            ResolvePlayerPrefabIfNeeded();

            NM.OnServerStarted += OnServerStarted;
            NM.OnClientConnectedCallback += OnClientConnected;
            NM.OnClientDisconnectCallback += OnClientDisconnected;
            if (NM.SceneManager != null) NM.SceneManager.OnSceneEvent += OnSceneEvent;

            StartCoroutine(Watchdog());
        }

        private void OnDisable()
        {
            if (s_Instance == this) s_Instance = null;
            if (NM == null) return;
            NM.OnServerStarted -= OnServerStarted;
            NM.OnClientConnectedCallback -= OnClientConnected;
            NM.OnClientDisconnectCallback -= OnClientDisconnected;
            if (NM.SceneManager != null) NM.SceneManager.OnSceneEvent -= OnSceneEvent;
        }

        private void OnServerStarted()
        {
            if (!NM.IsServer) return;
#if UNITY_EDITOR
            Debug.Log("[PlayerSpawner] Server started. ConnectedIds=" + NM.ConnectedClientsIds.Count);
#endif
            foreach (var cid in NM.ConnectedClientsIds)
            {
                if (cid == NetworkManager.ServerClientId) continue;
                TrySpawn(cid, "ServerStarted");
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (!NM.IsServer) return;
            if (clientId == NetworkManager.ServerClientId) return;
#if UNITY_EDITOR
            Debug.Log($"[PlayerSpawner] OnClientConnected {clientId}");
#endif
            TrySpawn(clientId, "ClientConnected");
            if (!_spawned.Contains(clientId)) StartCoroutine(RetrySpawnIfMissing(clientId));
        }

        private void OnClientDisconnected(ulong clientId)
        {
            _spawned.Remove(clientId);
            _pending.Remove(clientId);
#if UNITY_EDITOR
            if (NM && NM.IsServer) Debug.Log($"[PlayerSpawner] OnClientDisconnected {clientId}");
#endif
        }

        private void OnSceneEvent(SceneEvent e)
        {
            if (!NM.IsServer) return;
            if (e.SceneEventType == SceneEventType.SynchronizeComplete)
            {
                if (e.ClientId == NetworkManager.ServerClientId) return;
#if UNITY_EDITOR
                Debug.Log($"[PlayerSpawner] SyncComplete {e.ClientId} scene:{e.SceneName}");
#endif
                TrySpawn(e.ClientId, $"SyncComplete:{e.SceneName}");
            }
        }

        private IEnumerator RetrySpawnIfMissing(ulong clientId)
        {
            for (int i = 0; i < 10 && !_spawned.Contains(clientId); i++)
            {
                yield return null;
                TrySpawn(clientId, $"Retry{i}");
            }
        }

        private IEnumerator Watchdog()
        {
            var wait = new WaitForSecondsRealtime(2f);
            while (true)
            {
                yield return wait;
                if (NM == null || !NM.IsServer) continue;

                var ids = NM.ConnectedClientsIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    var cid = ids[i];
                    if (cid == NetworkManager.ServerClientId) continue;
                    if (_spawned.Contains(cid)) continue;
                    TrySpawn(cid, "Watchdog");
                }
            }
        }

        private void TrySpawn(ulong clientId, string reason)
        {
            if (!NM.IsServer) return;
            if (clientId == NetworkManager.ServerClientId) return;

            if (playerPrefab == null)
            {
#if UNITY_EDITOR
                Debug.LogError("[PlayerSpawner] Player prefab not assigned/resolved.");
#endif
                return;
            }

            if (_spawned.Contains(clientId) || _pending.Contains(clientId)) return;

            if (!NM.ConnectedClients.TryGetValue(clientId, out var client))
            {
#if UNITY_EDITOR
                Debug.LogWarning($"[PlayerSpawner] Client {clientId} not in ConnectedClients yet ({reason}).");
#endif
                return;
            }

            if (client.PlayerObject && client.PlayerObject.IsSpawned)
            {
                _spawned.Add(clientId);
                return;
            }

            foreach (var no in NM.SpawnManager.SpawnedObjectsList)
            {
                if (no != null && no.IsSpawned && no.OwnerClientId == clientId && no.GetComponent<PlayerNetwork>() != null)
                {
                    _spawned.Add(clientId);
                    return;
                }
            }

            _pending.Add(clientId);

            var (pos, rot) = GetSpawnPose((int)clientId);
            var inst = Instantiate(playerPrefab, pos, rot);
            EnsurePrefabRegistered(inst);
            inst.SpawnAsPlayerObject(clientId);

            _pending.Remove(clientId);
            _spawned.Add(clientId);

#if UNITY_EDITOR
            Debug.Log($"[PlayerSpawner] Spawned Player for {clientId} ({reason}) at {pos}");
#endif
        }

        private (Vector3 pos, Quaternion rot) GetSpawnPose(int seed)
        {
            if (spawnPoints != null && spawnPoints.Count > 0)
            {
                int i = Mathf.Abs(seed) % spawnPoints.Count;
                var t = spawnPoints[i] ? spawnPoints[i] : transform;
                return (t.position, t.rotation);
            }

            var a = ((seed * 137) % 360) * Mathf.Deg2Rad;
            var r = Mathf.Max(0.5f, randomRadius);
            var pos = transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
            var rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            return (pos, rot);
        }

        // ---------- Prefab resolution/registration ----------

        private void ResolvePlayerPrefabIfNeeded()
        {
            if (playerPrefab != null) { EnsurePrefabRegistered(playerPrefab); return; }

            var nm = NM;
            if (nm == null) return;

            var resolved = FindPrefabInNetworkConfigViaReflection(nm) ?? FindAnyLoadedPlayerPrefab();
            if (resolved != null)
            {
                playerPrefab = resolved;
                EnsurePrefabRegistered(playerPrefab);
#if UNITY_EDITOR
                Debug.Log("[PlayerSpawner] Auto-resolved player prefab: " + playerPrefab.name);
#endif
            }
            else
            {
                Debug.LogError("[PlayerSpawner] Could not auto-resolve a player prefab. Assign one in inspector and ensure it's in Network Prefabs.");
            }
        }

        // Works across NGO versions by reflecting the container and its list entries.
        private static NetworkObject FindPrefabInNetworkConfigViaReflection(NetworkManager nm)
        {
            var prefabsContainer = nm.NetworkConfig == null ? null : nm.NetworkConfig.Prefabs;
            if (prefabsContainer == null) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            IEnumerable list = null;

            // Try properties that look like a prefab list
            foreach (var prop in prefabsContainer.GetType().GetProperties(flags))
            {
                if (!typeof(IEnumerable).IsAssignableFrom(prop.PropertyType)) continue;
                if (!prop.Name.ToLower().Contains("prefab")) continue;
                list = prop.GetValue(prefabsContainer) as IEnumerable;
                if (list != null) break;
            }
            // Try fields if properties didn't work
            if (list == null)
            {
                foreach (var field in prefabsContainer.GetType().GetFields(flags))
                {
                    if (!typeof(IEnumerable).IsAssignableFrom(field.FieldType)) continue;
                    if (!field.Name.ToLower().Contains("prefab")) continue;
                    list = field.GetValue(prefabsContainer) as IEnumerable;
                    if (list != null) break;
                }
            }
            if (list == null) return null;

            foreach (var item in list)
            {
                if (item == null) continue;
                var it = item.GetType();
                var prefabProp = it.GetProperty("Prefab", flags);
                var go = prefabProp?.GetValue(item) as GameObject;
                if (!go) continue;
                var no = go.GetComponent<NetworkObject>();
                if (!no) continue;
                if (go.GetComponent<PlayerNetwork>() != null) return no;
            }
            return null;
        }

        private static NetworkObject FindAnyLoadedPlayerPrefab()
        {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var all = Resources.FindObjectsOfTypeAll<NetworkObject>();
#else
            var all = Resources.FindObjectsOfTypeAll(typeof(NetworkObject)) as NetworkObject[];
#endif
            for (int i = 0; i < all.Length; i++)
            {
                var no = all[i];
                if (!no) continue;
                if (no.gameObject.scene.IsValid()) continue; // skip scene instances
                if (no.GetComponent<PlayerNetwork>() != null) return no;
            }
            return null;
        }

        private void EnsurePrefabRegistered(NetworkObject no)
        {
            if (!no) return;
            var nm = NM; if (nm == null) return;
            try { nm.AddNetworkPrefab(no.gameObject); } catch { /* already registered */ }
        }
    }
}
