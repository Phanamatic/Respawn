using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

namespace Game.Net
{
    [DefaultExecutionOrder(-10000)]
    public sealed class LobbyPlayerSpawner : MonoBehaviour
    {
        [Header("Player Prefab (required)")]
        [SerializeField] private NetworkObject playerPrefab;

        [Header("Spawn Sources")]
        [SerializeField] private Transform spawnParent;                 // optional container
        [SerializeField] private string spawnPointTag = "PlayerSpawn";  // optional tag

        [Header("Spawn Points (auto-filled)")]
        [SerializeField] private List<Transform> spawnPoints = new();

        [Header("Fallback Ring")]
        [SerializeField, Min(0f)] private float randomRadius = 4f;

        [Header("Grounding")]
        [SerializeField] LayerMask groundMask = 1 << 6;
        [SerializeField, Min(0.001f)] float groundSkin = 0.02f;
        [SerializeField, Min(0.1f)] float probeUp = 10f;
        [SerializeField, Min(0.1f)] float probeDown = 50f;

        private int _nextIndex;
        NetworkManager NM => NetworkManager.Singleton;

        void OnEnable()
        {
            NM.OnServerStarted += OnServerStarted;
            NM.OnClientConnectedCallback += OnClientConnected;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            if (NM != null)
            {
                NM.OnServerStarted -= OnServerStarted;
                NM.OnClientConnectedCallback -= OnClientConnected;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnServerStarted()
        {
            if (!NM.IsServer) return;
            // Force-spawn for any clients that were already connected when this scene loaded.
            foreach (var kvp in NM.ConnectedClientsIds)
                TrySpawn(kvp, "ServerStarted");
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (!NM || !NM.IsServer) return;
            if (s.name == "Lobby")
            {
                foreach (var id in NM.ConnectedClientsIds)
                    TrySpawn(id, "SceneLoaded");
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            TrySpawn(clientId, "ClientConnected");
        }

        void TrySpawn(ulong clientId, string reason)
        {
            if (!NM.IsServer) return;

            // Resolve prefab: serialized field first, then NetworkConfig.PlayerPrefab
            var prefab = playerPrefab;
            if (!prefab)
            {
                var cfgPlayer = (NM != null && NM.NetworkConfig != null) ? NM.NetworkConfig.PlayerPrefab : null;
                if (cfgPlayer) prefab = cfgPlayer.GetComponent<NetworkObject>();
            }

            if (!prefab)
            {
                Debug.LogError("[LobbyPlayerSpawner] No Player prefab set. Assign it on the spawner or set NetworkConfig.PlayerPrefab (and ensure it is in NetworkPrefabs).");
                return;
            }

            // Already has a PlayerObject? Do nothing.
            if (NM.ConnectedClients.TryGetValue(clientId, out var cc) && cc.PlayerObject && cc.PlayerObject.IsSpawned)
                return;

            if (spawnPoints == null || spawnPoints.Count == 0) RefreshSpawnPoints();

            var pose = GetSpawnPose();

            var inst = Instantiate(prefab, pose.pos, pose.rot);

            // Ensure prefab instances are active before NGO spawn; prefab asset may be saved inactive for editor previews.
            if (!inst.gameObject.activeSelf)
                inst.gameObject.SetActive(true);

            // Ground and depenetrate before Spawn()
            var capsule = inst.GetComponent<CapsuleCollider>();
            AdjustToGroundAndResolve(inst.transform, capsule);

            // CRITICAL: Set phase to Lobby BEFORE spawn so OnNetworkSpawn sees the correct phase immediately.
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn) pn.SeedPhasePreSpawnServer(PlayerPhase.Lobby);

            inst.SpawnAsPlayerObject(clientId);

#if UNITY_EDITOR
            Debug.Log($"[LobbyPlayerSpawner] Spawned {clientId} at {inst.transform.position} ({reason})");
#endif
        }

        (Vector3 pos, Quaternion rot) GetSpawnPose()
        {
            if (spawnPoints != null && spawnPoints.Count > 0)
            {
                if (_nextIndex >= spawnPoints.Count || _nextIndex < 0) _nextIndex = 0;
                var t = spawnPoints[_nextIndex] ? spawnPoints[_nextIndex] : transform;
                _nextIndex = (_nextIndex + 1) % Mathf.Max(1, spawnPoints.Count);
                return (t.position, t.rotation);
            }

            float a = (_nextIndex * 137) % 360 * Mathf.Deg2Rad;
            _nextIndex++;
            float r = Mathf.Max(0.5f, randomRadius);
            var pos = transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
            return (pos, Quaternion.identity);
        }

        void RefreshSpawnPoints()
        {
            var list = new List<Transform>();

            if (spawnParent)
                foreach (var t in spawnParent.GetComponentsInChildren<Transform>(true))
                    if (t && t != spawnParent) list.Add(t);

            if (list.Count == 0 && !string.IsNullOrWhiteSpace(spawnPointTag))
            {
                var tagged = GameObject.FindGameObjectsWithTag(spawnPointTag);
                if (tagged != null && tagged.Length > 0) list.AddRange(tagged.Select(g => g.transform));
            }

            if (list.Count == 0 && spawnPoints != null && spawnPoints.Count > 0)
                list.AddRange(spawnPoints.Where(t => t));

            spawnPoints = list.Where(t => t != null).Distinct().ToList();
#if UNITY_EDITOR
            Debug.Log($"[LobbyPlayerSpawner] Spawn points: {spawnPoints.Count}");
#endif
        }

        void AdjustToGroundAndResolve(Transform t, CapsuleCollider capsule)
        {
            // Snap to ground below
            var pos = t.position;
            float lift = ComputeLift(capsule, groundSkin);
            var start = pos + Vector3.up * probeUp;
            if (Physics.Raycast(start, Vector3.down, out var hit, probeUp + probeDown, groundMask, QueryTriggerInteraction.Ignore))
                pos.y = hit.point.y + lift;
            t.position = pos;

            // Resolve initial penetration
            if (!capsule) return;
            const int maxIters = 5;
            int iter = 0;
            while (iter++ < maxIters)
            {
                GetCapsuleWorld(capsule, t, out var p0, out var p1, out var r);
                var overlaps = Physics.OverlapCapsule(p0, p1, r, groundMask, QueryTriggerInteraction.Ignore);
                if (overlaps == null || overlaps.Length == 0) break;

                Vector3 total = Vector3.zero;
                foreach (var col in overlaps)
                {
                    if (col.attachedRigidbody && col.attachedRigidbody == capsule.attachedRigidbody) continue;
                    if (Physics.ComputePenetration(capsule, t.position, t.rotation, col, col.transform.position, col.transform.rotation, out var dir, out var dist))
                        if (dist > 0f) total += dir * dist;
                }
                if (total.sqrMagnitude < 1e-6f) break;
                if (total.y < 0f) total.y = 0f; // never push downward into ground
                t.position += total + Vector3.up * groundSkin;
            }
        }

        static float ComputeLift(CapsuleCollider c, float skin)
        {
            if (!c) return Mathf.Max(0.01f, skin);
            float half = Mathf.Max(0f, c.height * 0.5f);
            return Mathf.Max(skin, half - c.radius + 0.01f);
        }

        static void GetCapsuleWorld(CapsuleCollider cap, Transform t, out Vector3 p0, out Vector3 p1, out float radius)
        {
            Vector3 center = t.TransformPoint(cap.center);
            float height = Mathf.Max(cap.height, cap.radius * 2f);
            float half = Mathf.Max(0f, height * 0.5f - cap.radius);
            Vector3 axis = cap.direction == 0 ? t.right : (cap.direction == 2 ? t.forward : t.up);
            p0 = center + axis * half;
            p1 = center - axis * half;

            var ls = t.lossyScale;
            if (cap.direction == 0) radius = cap.radius * Mathf.Max(ls.y, ls.z);
            else if (cap.direction == 2) radius = cap.radius * Mathf.Max(ls.x, ls.y);
            else radius = cap.radius * Mathf.Max(ls.x, ls.z);
        }
    }
}
