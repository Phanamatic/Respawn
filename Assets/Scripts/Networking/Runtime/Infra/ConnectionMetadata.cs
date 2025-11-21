// Assets/Scripts/Networking/Runtime/Infra/ConnectionMetadata.cs
// Stores connection payload metadata (e.g., spectator flag) and installs a permissive approval callback.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    public static class ConnectionMetadataBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateInstaller()
        {
            var existing = GameObject.Find(nameof(ConnectionMetadataInstaller));
            if (existing != null) return;

            var go = new GameObject(nameof(ConnectionMetadataInstaller));
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ConnectionMetadataInstaller>();
        }
    }

    [Serializable]
    public struct ConnectionPayloadData
    {
        public string displayName;
        public bool spectator;
    }

    /// <summary>
    /// Central cache for connection metadata and a lightweight approval hook so servers can treat spectators separately.
    /// </summary>
    public static class ConnectionMetadata
    {
        private static readonly Dictionary<ulong, ConnectionPayloadData> s_ClientPayloads = new();
        private static ConnectionPayloadData s_LocalPayload;

        public static bool LocalIsSpectator => s_LocalPayload.spectator;

        public static void SetLocalPayload(ConnectionPayloadData payload, NetworkManager nm = null)
        {
            s_LocalPayload = payload;
            var manager = nm ? nm : NetworkManager.Singleton;
            if (manager != null && manager.NetworkConfig != null)
            {
                manager.NetworkConfig.ConnectionData = Encode(payload);
            }
        }

        internal static byte[] Encode(ConnectionPayloadData payload)
        {
            try
            {
                var json = JsonUtility.ToJson(payload);
                return Encoding.UTF8.GetBytes(json);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        internal static ConnectionPayloadData Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return default;
            try
            {
                var json = Encoding.UTF8.GetString(data);
                return JsonUtility.FromJson<ConnectionPayloadData>(json);
            }
            catch
            {
                return default;
            }
        }

        internal static void Register(ulong clientId, ConnectionPayloadData payload)
        {
            s_ClientPayloads[clientId] = payload;
        }

        internal static void Remove(ulong clientId) => s_ClientPayloads.Remove(clientId);

        internal static void ClearAll() => s_ClientPayloads.Clear();

        public static bool IsSpectator(ulong clientId)
        {
            return s_ClientPayloads.TryGetValue(clientId, out var payload) && payload.spectator;
        }

        public static ConnectionPayloadData GetPayload(ulong clientId)
        {
            s_ClientPayloads.TryGetValue(clientId, out var payload);
            return payload;
        }
    }

    /// <summary>
    /// Runtime installer that enables connection approval and stashes spectator metadata server-side.
    /// </summary>
    public sealed class ConnectionMetadataInstaller : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Install();
        }

        void OnDestroy()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.ConnectionApprovalCallback -= OnApproval;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;
                nm.OnServerStopped -= OnServerStopped;
            }
        }

        void Install()
        {
            var nm = NetworkManager.Singleton;
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (nm == null) nm = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
#else
            if (nm == null) nm = FindObjectOfType<NetworkManager>();
#endif
            if (nm == null)
            {
                StartCoroutine(WaitAndInstall());
                return;
            }

            Configure(nm);
        }

        IEnumerator WaitAndInstall()
        {
            float t = 0f;
            const float timeout = 15f;
            NetworkManager nm = null;
            while (t < timeout && nm == null)
            {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
                nm = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
#else
                nm = FindObjectOfType<NetworkManager>();
#endif
                if (nm != null) break;
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (nm != null) Configure(nm);
        }

        void Configure(NetworkManager nm)
        {
            if (nm.NetworkConfig != null)
                nm.NetworkConfig.ConnectionApproval = true;

            nm.ConnectionApprovalCallback -= OnApproval;
            nm.ConnectionApprovalCallback += OnApproval;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            nm.OnServerStopped -= OnServerStopped;
            nm.OnServerStopped += OnServerStopped;
        }

        static void OnApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            var payload = ConnectionMetadata.Decode(request.Payload);
            ConnectionMetadata.Register(request.ClientNetworkId, payload);

            response.Approved = true;
            response.CreatePlayerObject = false; // PlayerObjects are spawned explicitly by match/lobby controllers.
            response.Pending = false;
        }

        static void OnClientDisconnected(ulong clientId)
        {
            ConnectionMetadata.Remove(clientId);
        }

        static void OnServerStopped(bool _)
        {
            ConnectionMetadata.ClearAll();
        }
    }
}
