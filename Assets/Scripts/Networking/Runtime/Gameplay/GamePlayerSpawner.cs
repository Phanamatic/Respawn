// Assets/Scripts/Networking/Runtime/Gameplay/GamePlayerSpawner.cs
using UnityEngine;
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
            inst.SpawnAsPlayerObject(clientId);

#if UNITY_EDITOR
            Debug.Log($"[GamePlayerSpawner] Spawned missing Player for {clientId} at {pos} ({reason})");
#endif
        }
    }
}
