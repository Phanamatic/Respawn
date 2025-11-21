// Assets/Scripts/Networking/Runtime/Gameplay/GamePlayerSpawner.cs
using UnityEngine;
using Game.Net; // use existing GroundClampServer
// Enables direct calls to GroundClampServer.TryFindNearestClearGround/SnapToGround if referenced here.
using Unity.Netcode;

namespace Game.Net
{
    [DefaultExecutionOrder(-10000)]
    public sealed class GamePlayerSpawner : MonoBehaviour
    {
        [Header("Player Prefab (required if direct-join)")]
        [SerializeField] private NetworkObject playerPrefab;

        [Header("Fallback spawn if needed")]
        [SerializeField] private Transform fallbackSpawn;
        [SerializeField, Min(0f)] private float yLift = 1.0f;

        NetworkManager NM => NetworkManager.Singleton;

        void OnEnable()
        {
            if (!NM || !NM.IsServer) return;
            NM.OnServerStarted += OnServerStarted;
            NM.OnClientConnectedCallback += OnClientConnected;
            if (NM.SceneManager != null) NM.SceneManager.OnSceneEvent += OnSceneEvent;
        }

        void OnDisable()
        {
            if (!NM) return;
            NM.OnServerStarted -= OnServerStarted;
            NM.OnClientConnectedCallback -= OnClientConnected;
            if (NM.SceneManager != null) NM.SceneManager.OnSceneEvent -= OnSceneEvent;
        }

        void OnServerStarted()
        {
            foreach (var cid in NM.ConnectedClientsIds)
                if (cid != NetworkManager.ServerClientId) EnsureExists(cid, "ServerStarted");
        }

        void OnClientConnected(ulong clientId)
        {
            if (clientId == NetworkManager.ServerClientId) return;
            EnsureExists(clientId, "ClientConnected");
        }

        void OnSceneEvent(SceneEvent e)
        {
            if (e.SceneEventType != SceneEventType.SynchronizeComplete) return;
            if (e.ClientId == NetworkManager.ServerClientId) return;
            EnsureExists(e.ClientId, $"SyncComplete:{e.SceneName}");
        }

        void EnsureExists(ulong clientId, string reason)
        {
            if (!NM.IsServer) return;

            if (Game.Net.ConnectionMetadata.IsSpectator(clientId))
            {
#if UNITY_EDITOR
                Debug.Log($"[GamePlayerSpawner] Skip spectator {clientId} ({reason})");
#endif
                return;
            }

            // If a Match controller is present, it owns spawning logic. Do not auto-spawn here.
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var match = FindFirstObjectByType<Game.Net.Match1v1Controller>(FindObjectsInactive.Exclude);
#else
            var match = FindObjectOfType<Game.Net.Match1v1Controller>(false);
#endif
            if (match != null) return;

            // Already has a PlayerObject? Do nothing; Match controller will place it later.
            if (NM.ConnectedClients.TryGetValue(clientId, out var cc) && cc.PlayerObject && cc.PlayerObject.IsSpawned)
                return;

            if (!playerPrefab)
            {
                Debug.LogWarning("[GamePlayerSpawner] No playerPrefab assigned, cannot create missing player.");
                return;
            }

            var pos = fallbackSpawn ? fallbackSpawn.position : transform.position;
            var rot = fallbackSpawn ? fallbackSpawn.rotation : Quaternion.identity;
            pos.y += yLift;

            var inst = Instantiate(playerPrefab, pos, rot);
            
            // CRITICAL: Set phase based on server type BEFORE spawn.
            // This fallback spawner is used when there's no Match controller, so detect server type.
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn)
            {
                var serverType = SessionContext.Type;
                var phase = (serverType == ServerType.OneVOne || serverType == ServerType.TwoVTwo) 
                    ? PlayerPhase.Match 
                    : PlayerPhase.Lobby;
                pn.SeedPhasePreSpawnServer(phase);
            }
            
            // Do NOT register instances at runtime; NetworkManager must already know this prefab.
            inst.SpawnAsPlayerObject(clientId);
            // Remove the runtime AddNetworkPrefab that was corrupting prefab tables.

#if UNITY_EDITOR
            Debug.Log($"[GamePlayerSpawner] Spawned missing Player for {clientId} at {pos} ({reason})");
#endif
        }
    }
}
