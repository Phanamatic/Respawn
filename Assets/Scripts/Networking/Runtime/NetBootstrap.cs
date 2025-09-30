// Assets/Scripts/Networking/Runtime/NetBootstrap.cs
// Set sensible 1v1/2v2 defaults so "waiting" means not full.

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
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport.Relay;
using System.Net;
using System.Net.Sockets;

namespace Game.Net
{
    public static class NetBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }

            var args = new Args(Environment.GetCommandLineArgs());

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

                int max = args.GetInt("-max", type == ServerType.Lobby ? 16 : type == ServerType.OneVOne ? 2 : 4);
                int threshold = args.GetInt("-threshold", type == ServerType.Lobby ? max / 2 : max);
                SessionContext.Configure(type, max, threshold);

                bool useDirect = args.GetStr("-net", "direct").ToLowerInvariant() == "direct";

                if (wantHost)
                {
                    string listenIp = args.GetStr("-listenIp", "");
                    string publishIp = args.GetStr("-publishIp", "");
                    int port = args.GetInt("-port", 0);

                    string region = args.GetStr("-region", "us-west1");

                    if (useDirect)
                    {
                        if (port == 0)
                        {
                            port = FindFreePort(50000, 60000);
                            if (port < 0)
                            {
                                Debug.LogError("[NetBootstrap] Could not find free port.");
                                return;
                            }
                        }

                        var listen = string.IsNullOrWhiteSpace(listenIp) ? "0.0.0.0" : listenIp;
                        utp.SetConnectionData(listen, (ushort)port);
                        Debug.Log($"[UTP] Server listen {listen}:{port}");
                        if (!nm.StartServer())
                        {
                            Debug.LogError("[NetBootstrap] StartServer failed.");
                            return;
                        }
                        var publish = string.IsNullOrWhiteSpace(publishIp) ? "127.0.0.1" : publishIp;
                        if (publish == "127.0.0.1")
                            Debug.LogWarning("[NetBootstrap] Direct mode will be LAN-only unless -publishIp is provided.");
                        SessionContext.SetSession(Guid.NewGuid().ToString(), publish + ":" + port);
                        Debug.Log($"[Direct] Hosting {type}. Publish={publish}:{port} Profile={UgsInitializer.CurrentProfile}");
                    }
                    else
                    {
                        // Relay host
                        try
                        {
                            var alloc = await RelayService.Instance.CreateAllocationAsync(max, region);
                            utp.SetRelayServerData(
                                alloc.RelayServer.IpV4,
                                (ushort)alloc.RelayServer.Port,
                                alloc.AllocationIdBytes,
                                alloc.Key,
                                alloc.ConnectionData,
                                null,
                                true);
                            if (!nm.StartServer())
                            {
                                Debug.LogError("[NetBootstrap] StartServer failed (relay).");
                                return;
                            }
                            var join = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                            SessionContext.SetSession(Guid.NewGuid().ToString(), join);
                            Debug.Log($"[Relay] Hosting {type}. JoinCode={join} Region={region} Profile={UgsInitializer.CurrentProfile}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError("[NetBootstrap] Relay allocate failed: " + ex);
                            return;
                        }
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
                        // Relay client
                        try
                        {
                            var joinAlloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
                            utp.SetRelayServerData(
                                joinAlloc.RelayServer.IpV4,
                                (ushort)joinAlloc.RelayServer.Port,
                                joinAlloc.AllocationIdBytes,
                                joinAlloc.Key,
                                joinAlloc.ConnectionData,
                                joinAlloc.HostConnectionData,
                                true);
                            Debug.Log($"[UTP] Client join via Relay. Code={joinCode}");
                            if (!nm.StartClient())
                            {
                                Debug.LogError("[NetBootstrap] StartClient failed (relay).");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError("[NetBootstrap] Relay join failed: " + ex);
                        }
                    }
                }
                else
                {
#if UNITY_EDITOR
                    Debug.Log("[NetBootstrap] No -mpsHost or -mpsJoin. Idle; UI will decide.");
#endif
                }
            }

            private int FindFreePort(int minPort, int maxPort)
            {
                for (int port = minPort; port <= maxPort; port++)
                {
                    TcpListener listener = null;
                    try
                    {
                        listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                        return port;
                    }
                    catch
                    {
                        // Port is busy
                    }
                    finally
                    {
                        listener?.Stop();
                    }
                }
                Debug.LogError("[NetBootstrap] No free port found in range " + minPort + "-" + maxPort);
                return -1;
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
