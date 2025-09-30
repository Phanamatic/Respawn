// Assets/Scripts/Networking/Runtime/NetworkConfigOptimizer.cs
// Version-agnostic NetworkConfig + UnityTransport tweaker with a small debug UI.
// Forces target FPS = 60 (vSync off) and defaults to a 60 Hz network tick.

using System.Reflection;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Game.Net
{
    [DefaultExecutionOrder(-10001)] // Run before PlayerSpawner
    public sealed class NetworkConfigOptimizer : MonoBehaviour
    {
        [Header("Performance Profile")]
        [Tooltip("Choose preset based on game type")]
        [SerializeField] private PerformanceProfile profile = PerformanceProfile.FastPacedShooter; // 60 Hz by default

        [Header("Transport Tweaks")]
        [SerializeField] private bool forceReliableSequenced = false;

        [Header("Custom Settings (if Custom profile)")]
        [SerializeField, Range(30, 256)] private int tickRate = 60;

        public enum PerformanceProfile
        {
            FastPacedShooter,  // 60 tick
            Competitive,       // 128 tick
            Balanced,          // 30 tick
            Custom             // Use manual settings
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (GameObject.Find("/NetworkConfigOptimizer") != null) return;
            var go = new GameObject("NetworkConfigOptimizer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<NetworkConfigOptimizer>();
        }

        private void Awake()
        {
            ApplyNetworkOptimizations();
            ApplyPhysicsAndFps();
        }

        private void ApplyPhysicsAndFps()
        {
            int targetTick = GetTargetTickRate();

            // Physics step synced to tick (safe default for top-down & arcade games)
            Time.fixedDeltaTime = 1f / targetTick;
            Time.maximumDeltaTime = Mathf.Clamp(Time.fixedDeltaTime * 2f, 0.01f, 0.05f);

            // Hard-requirement: target FPS must be exactly 60
            QualitySettings.vSyncCount = 0;         // disable vSync so targetFrameRate is respected
            Application.targetFrameRate = 60;       // strict 60 fps

#if UNITY_EDITOR
            Debug.Log($"[NetworkConfigOptimizer] Physics {Mathf.RoundToInt(1f/Time.fixedDeltaTime)} Hz, TargetFPS={Application.targetFrameRate} (vSync={QualitySettings.vSyncCount})");
#endif
        }

        private int GetTargetTickRate()
        {
            switch (profile)
            {
                case PerformanceProfile.FastPacedShooter: return 60;
                case PerformanceProfile.Competitive:      return 128;
                case PerformanceProfile.Balanced:         return 30;
                case PerformanceProfile.Custom:           return Mathf.Clamp(tickRate, 30, 256);
                default: return 60;
            }
        }

        private void ApplyNetworkOptimizations()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
                nm = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
#else
                nm = FindObjectOfType<NetworkManager>(true);
#endif
            }

            if (nm == null)
            {
                Debug.LogError("[NetworkConfigOptimizer] No NetworkManager found!");
                return;
            }

            if (nm.NetworkConfig == null)
                nm.NetworkConfig = new NetworkConfig();

            var config = nm.NetworkConfig;
            int targetTick = GetTargetTickRate();

            // Tick & timing (cover old/new property names)
            SetConfigProperty(config, "TickRate", (uint)targetTick);
            SetConfigProperty(config, "NetworkTickIntervalSec", 1f / targetTick);

            // Timeouts
            SetConfigProperty(config, "SpawnTimeout", 1.0f);
            SetConfigProperty(config, "PlayerSpawnTimeout", 1.0f);
            SetConfigProperty(config, "ConnectTimeout", 10f);
            SetConfigProperty(config, "ConnectionApprovalTimeout", 10f);
            SetConfigProperty(config, "DisconnectTimeout", 6f);
            SetConfigProperty(config, "KeepAliveTimeout", 5f);

            // Send pacing
            SetConfigProperty(config, "MessageSendRate", targetTick * 2);
            SetConfigProperty(config, "SendRate", targetTick * 2);
            SetConfigProperty(config, "SendTickrate", targetTick * 2);
            SetConfigProperty(config, "ClientSendInterval", 1f / (targetTick * 2));
            SetConfigProperty(config, "ServerSendInterval", 1f / (targetTick * 2));

            // NGO settings
            config.EnableSceneManagement = true;
            config.ForceSamePrefabs = false;
            config.RecycleNetworkIds = true;
            config.NetworkIdRecycleDelay = 1f;
            config.RpcHashSize = HashSize.VarIntFourBytes;

            // Logging
            SetConfigProperty(config, "EnableNetworkLogs", false);
            SetConfigProperty(config, "NetworkLogging", false);

            // Transport tuning
            ConfigureUnityTransport(nm, targetTick);

#if UNITY_EDITOR
            Debug.Log($"[NetworkConfigOptimizer] Applied {profile} profile. TickRate={targetTick}Hz");
#endif
        }

        private static void SetConfigProperty(object obj, string name, object value)
        {
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var prop = t.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(obj, value); return; } catch { }
            }

            var field = t.GetField(name, flags);
            if (field != null)
            {
                try { field.SetValue(obj, value); } catch { }
            }
        }

        private void ConfigureUnityTransport(NetworkManager nm, int targetTick)
        {
            var utp = nm.GetComponent<UnityTransport>();
            if (!utp)
            {
#if UNITY_EDITOR
                Debug.LogWarning("[NetworkConfigOptimizer] UnityTransport not found on NetworkManager");
#endif
                return;
            }

            utp.HeartbeatTimeoutMS = 2000;
            utp.ConnectTimeoutMS = 5000;
            utp.MaxConnectAttempts = 8;
            utp.DisconnectTimeoutMS = 6000;

            SetUTPProperty(utp, "MaxPacketQueueSize", 256);
            SetUTPProperty(utp, "MaxSendQueueSize",   Mathf.Clamp(targetTick * 4, 256, 4096));
            SetUTPProperty(utp, "MaxReceiveQueueSize",Mathf.Clamp(targetTick * 4, 256, 4096));
            SetUTPProperty(utp, "MaxPacketSize", 1400);

            if (forceReliableSequenced) SetUTPReliabilityMode(utp);
        }

        private static void SetUTPProperty(UnityTransport utp, string name, object value)
        {
            var t = utp.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var prop = t.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(utp, value); return; } catch { }
            }

            var field = t.GetField(name, flags);
            if (field != null)
            {
                try { field.SetValue(utp, value); } catch { }
            }
        }

        private static void SetUTPReliabilityMode(UnityTransport utp)
        {
            var t = utp.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var prop = t.GetProperty("ProtocolType", flags);
            if (prop != null && prop.PropertyType.IsEnum)
            {
                try
                {
                    var val = System.Enum.Parse(prop.PropertyType, "ReliableSequenced");
                    prop.SetValue(utp, val);
                }
                catch { /* keep default */ }
            }
        }
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Lightweight in-game HUD (toggle with F9).
    public static class NetworkPerformanceUI
    {
        private static bool _showUI;
        private static Rect _windowRect = new Rect(10, 10, 320, 220);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (GameObject.Find("/NetworkPerformanceUI") != null) return;
            var go = new GameObject("NetworkPerformanceUI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PerformanceUIRenderer>();
        }

        private class PerformanceUIRenderer : MonoBehaviour
        {
            void Update()
            {
                if (Input.GetKeyDown(KeyCode.F9)) _showUI = !_showUI;
            }

            void OnGUI()
            {
                if (!_showUI) return;
                _windowRect = GUI.Window(0xBEEF, _windowRect, DrawWindow, "Network Performance");
            }

            void DrawWindow(int id)
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || (!nm.IsServer && !nm.IsClient))
                {
                    GUILayout.Label("Network inactive");
                }
                else
                {
                    GUILayout.Label($"Mode: {(nm.IsServer ? "Server" : "Client")}");
                    GUILayout.Label($"Peers (w/o server): {nm.ConnectedClientsIds.Count - (nm.IsServer ? 1 : 0)}");

                    uint tick = 0;
                    try { tick = nm.NetworkConfig != null ? nm.NetworkConfig.TickRate : 0; } catch { }
                    if (tick > 0) GUILayout.Label($"Tick: {tick} Hz");

                    int phys = Mathf.RoundToInt(1f / Mathf.Max(0.0001f, Time.fixedDeltaTime));
                    int fps  = (int)Mathf.Round(1f / Mathf.Max(0.00001f, Time.smoothDeltaTime));
                    GUILayout.Label($"Physics: {phys} Hz");
                    GUILayout.Label($"FPS: {fps}");
                }

                GUILayout.Space(4);
                GUILayout.Label("F9 to toggle");
                GUI.DragWindow();
            }
        }
    }
#endif
}
