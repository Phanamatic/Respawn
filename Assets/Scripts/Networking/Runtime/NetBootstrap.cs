// Assets/Scripts/Networking/Runtime/NetBootstrap.cs
// Unity 6 (6000.0.52f1)
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Core;
using Unity.Services.Authentication;

namespace Game.Net
{
    public static class NetBootstrap
    {
        // Run after the first scene loads so scene objects (NetworkManager, UnityTransport) exist.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            // Cap non-server builds to 60 FPS (servers use Null device).
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }

            var args = new Args(Environment.GetCommandLineArgs());

            // Always create the runner. It will wait for NM/UTP safely.
            var go = new GameObject("MpsBootstrapRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MpsBootstrapRunner>().Run(args);
        }

        internal readonly struct Args
        {
            private readonly string[] _all;
            public Args(string[] all) { _all = all ?? Array.Empty<string>(); }
            public bool HasFlag(string flag) => _all.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            public int GetInt(string key, int fallback)
            { var s = GetStr(key, (string)null); return int.TryParse(s, out var v) ? v : fallback; }
            public string GetStr(string key, string fallback)
            {
                for (int i = 0; i < _all.Length - 1; i++)
                    if (string.Equals(_all[i], key, StringComparison.OrdinalIgnoreCase))
                        return _all[i + 1];
                return fallback;
            }
        }

        private static System.Collections.IEnumerator CoLoadSceneNextFrame(string sceneName)
        {
            yield return null;
            var nm = NetworkManager.Singleton;
            if (!nm) yield break;

            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[NetBootstrap] Scene '{sceneName}' not in Build Settings.");
                yield break;
            }

            if (nm.IsServer) nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        }

        private sealed class MpsBootstrapRunner : MonoBehaviour
        {
            bool _started;

            public void Run(Args args)
            {
                if (_started) return;
                _started = true;
                _ = RunAsync(args);
            }

            private async Task RunAsync(Args args)
            {
                // --- UGS init with persistent, unique profile ---
                var env = args.GetStr("-env", "production");

                const string kPrefsKey = "ugs_profile_install";
                var installId = PlayerPrefs.GetString(kPrefsKey, "");
                if (string.IsNullOrEmpty(installId))
                {
                    installId = Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(kPrefsKey, installId);
                    PlayerPrefs.Save();
                }

                var profileCli = args.GetStr("-profile", null);
                var defaultProfile = Application.isEditor ? "Editor" : $"Client-{installId}";
                var profile = string.IsNullOrWhiteSpace(profileCli) ? defaultProfile : profileCli.Trim();

                Debug.Log("[NetBootstrap] Starting UGS initialization...");
                await UgsInitializer.EnsureAsync(env, profile);
                if (!UgsInitializer.IsReady)
                {
                    Debug.LogWarning("[NetBootstrap] UGS init failed, retrying...");
                    await Task.Delay(1500);
                    await UgsInitializer.RetryAsync(env, profile);
                }
                if (!UgsInitializer.IsReady)
                {
                    Debug.LogError("[NetBootstrap] UGS not ready: " + (UgsInitializer.LastError ?? "unknown"));
                } else {
                    Debug.Log("[NetBootstrap] UGS initialized successfully.");
                }

                // --- Wait for NetworkManager + UnityTransport to exist and be wired ---
                NetworkManager nm = null;
                UnityTransport utp = null;

                var waitStart = Time.realtimeSinceStartup;
                const float waitTimeout = 20f;

                while ((nm = NetworkManager.Singleton) == null)
                {
                    if (Time.realtimeSinceStartup - waitStart > waitTimeout)
                    {
                        Debug.LogError("[NetBootstrap] Timed out waiting for NetworkManager.Singleton.");
                        return;
                    }
                    await Task.Yield();
                }

                // Ensure a UnityTransport is present and assigned.
                while (!nm.TryGetComponent(out utp))
                {
                    utp = nm.GetComponent<UnityTransport>();
                    if (utp) break;
                    if (Time.realtimeSinceStartup - waitStart > waitTimeout)
                    {
                        Debug.LogError("[NetBootstrap] UnityTransport not found on NetworkManager.");
                        return;
                    }
                    await Task.Yield();
                }

                if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
                if (nm.NetworkConfig.NetworkTransport == null) nm.NetworkConfig.NetworkTransport = utp;

                // De-dupe network prefabs in a version-agnostic way (compare prefab refs).
                SanitizeNetworkPrefabs(nm);

                // --- CLI parsing ---
                bool wantHost = args.HasFlag("-mpsHost");
                string joinCode = args.GetStr("-mpsJoin", null);
                bool allowClientAutoJoin = args.HasFlag("-autoJoin"); // UI controls join unless true

                string serverTypeStr = args.GetStr("-serverType", "lobby").ToLowerInvariant();
                var type = serverTypeStr == "1v1" ? ServerType.OneVOne : serverTypeStr == "2v2" ? ServerType.TwoVTwo : ServerType.Lobby;

                int max = args.GetInt("-max", type == ServerType.Lobby ? 16 : type == ServerType.OneVOne ? 3 : 5);
                int threshold = args.GetInt("-threshold", max / 2);
                SessionContext.Configure(type, max, threshold);

                bool useDirect = args.GetStr("-net", "direct").ToLowerInvariant() == "direct";

                if (wantHost)
                {
                    string listenIp = args.GetStr("-listenIp", "");
                    string publishIp = args.GetStr("-publishIp", "");
                    int port = args.GetInt("-port", 7777);
                    string region = args.GetStr("-region", "us-west1");

                    if (useDirect)
                    {
                        var listen = string.IsNullOrWhiteSpace(listenIp) ? "0.0.0.0" : listenIp;
                        utp.SetConnectionData(listen, (ushort)port);
                        Debug.Log($"[UTP] Server listen {listen}:{port}");
                        if (!nm.StartServer())
                        {
                            Debug.LogError("[NetBootstrap] StartServer failed.");
                            return;
                        }
                        var publish = string.IsNullOrWhiteSpace(publishIp) ? "127.0.0.1" : publishIp;
                        SessionContext.SetSession(Guid.NewGuid().ToString(), publish + ":" + port);
                        Debug.Log($"[Direct] Hosting {type}. Publish={publish}:{port} Profile={UgsInitializer.CurrentProfile}");
                    }
                    else
                    {
                        Debug.Log("[NetBootstrap] Relay mode selected, but simplified to direct. Use -net direct for direct mode.");
                    }

                    var sceneName = args.GetStr("-scene", string.Empty);
                    if (!string.IsNullOrWhiteSpace(sceneName)) StartCoroutine(CoLoadSceneNextFrame(sceneName));
                }
                else if (!string.IsNullOrEmpty(joinCode))
                {
                    if (!allowClientAutoJoin)
                    {
                        Debug.Log("[NetBootstrap] UI-join mode: ignoring -mpsJoin (use -autoJoin to enable CLI join).");
                        return;
                    }

                    if (useDirect)
                    {
                        var parts = joinCode.Split(':');
                        if (parts.Length != 2 || !ushort.TryParse(parts[1], out ushort p))
                        {
                            Debug.LogError("[NetBootstrap] Invalid direct join code: " + joinCode);
                            return;
                        }
                        utp.SetConnectionData(parts[0], p);
                        Debug.Log($"[UTP] Client connect {parts[0]}:{p}");
                        if (!nm.StartClient())
                        {
                            Debug.LogError("[NetBootstrap] StartClient failed.");
                            return;
                        }
                    }
                    else
                    {
                        Debug.Log("[NetBootstrap] Relay join selected, but simplified to direct.");
                    }
                }
                else
                {
#if UNITY_EDITOR
                    Debug.Log("[NetBootstrap] No -mpsHost or -mpsJoin. Idle; UI will decide.");
#endif
                }
            }

            private static void SanitizeNetworkPrefabs(NetworkManager nm)
            {
                try
                {
                    var prefabs = nm.NetworkConfig?.Prefabs;
                    var listProp = prefabs?.GetType().GetProperty("PrefabList");
                    var list = listProp?.GetValue(prefabs) as System.Collections.IList;
                    if (list == null) return;

                    var seen = new HashSet<UnityEngine.Object>();
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var entry = list[i];
                        var entryType = entry.GetType();

                        UnityEngine.Object prefab =
                            (UnityEngine.Object)(entryType.GetProperty("Prefab")?.GetValue(entry))
                            ?? (UnityEngine.Object)(entryType.GetProperty("SourcePrefabToOverride")?.GetValue(entry));

                        if (!prefab) continue;
                        if (!seen.Add(prefab))
                        {
                            Debug.LogWarning($"[NetBootstrap] Removing duplicate NetworkPrefab entry: {prefab.name}");
                            list.RemoveAt(i);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[NetBootstrap] Prefab sanitize skipped: " + e.Message);
                }
            }
        }
    }

    internal static class UgsInitializer
    {
        private static Task _initTask;
        public static string CurrentProfile { get; private set; } = "Default";
        public static bool IsReady { get; private set; }
        public static string LastError { get; private set; }

        public static Task EnsureAsync(string environmentName = "production", string profile = "Default")
        {
            if (_initTask != null) return _initTask;
            CurrentProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile;
            _initTask = InitializeAsync(environmentName, CurrentProfile);
            return _initTask;
        }

        public static async Task RetryAsync(string environmentName, string profile)
        {
            if (IsReady) return;
            CurrentProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile;
            _initTask = InitializeAsync(environmentName, CurrentProfile);
            await _initTask;
        }

        private static async Task InitializeAsync(string environmentName, string profile)
        {
            IsReady = false;
            LastError = null;

            try
            {
                Debug.Log($"[UGS] Initializing Unity Services with env: {environmentName}, profile: {profile}");
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    var options = new InitializationOptions()
                        .SetOption("com.unity.services.core.environment-name", environmentName)
                        .SetProfile(profile);
                    await UnityServices.InitializeAsync(options);
                    Debug.Log("[UGS] Unity Services initialized.");
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    Debug.Log("[UGS] Signing in anonymously...");
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    Debug.Log("[UGS] Signed in anonymously.");
                }

                IsReady = true;
                Debug.Log($"[UGS] Init OK. ProjectId={Application.cloudProjectId}, Env={environmentName}, Profile={profile}, PlayerId={AuthenticationService.Instance.PlayerId}");
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Debug.LogError("[UGS] Init failed: " + ex);
            }
        }
    }
}
