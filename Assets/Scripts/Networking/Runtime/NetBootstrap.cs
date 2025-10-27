// Assets/Scripts/Networking/Runtime/NetBootstrap.cs
// Unity 6 (6000.0.52f1) – Direct host/client bootstrap with profile sanitization.

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
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
// LAN IP resolve for lobby data
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

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

            // Returns the first site-local IPv4 that is up and not virtual/tunnel.
            private static string ResolveLocalIPv4()
            {
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                        var ipProps = ni.GetIPProperties();
                        foreach (var ua in ipProps.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            var ip = ua.Address;
                            byte[] b = ip.GetAddressBytes();
                            // 10.x.x.x or 172.16-31.x.x or 192.168.x.x
                            bool privateA = b[0] == 10;
                            bool privateB = b[0] == 172 && b[1] >= 16 && b[1] <= 31;
                            bool privateC = b[0] == 192 && b[1] == 168;
                            if (privateA || privateB || privateC) return ip.ToString();
                        }
                    }
                }
                catch { }
                return null;
            }

            public void Run(Args args)
            {
                if (_started) return;
                _started = true;
                _ = RunAsync(args);
            }

            private static string SanitizeProfile(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "Default";
                var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
                if (safe.Length > 30) safe = safe.Substring(0, 30);
                return string.IsNullOrWhiteSpace(safe) ? "Default" : safe;
            }

            private static int ComputeLobbyCapacity(string serverType, int playerCap)
            {
                // Normalize type
                string t = (serverType ?? string.Empty).Trim().ToLowerInvariant();

                // Map to known player caps when caller passes only "max" from CLI:
                // lobby: 16 players, 1v1: 2 players, 2v2: 4 players.
                int nonServerCap = t switch
                {
                    "lobby" => 16,
                    "1v1" or "onevone" or "match_1v1" => 2,
                    "2v2" or "twovtwo" or "match_2v2" => 4,
                    _ => playerCap > 0 ? playerCap : 16
                };

                // Add one slot for the dedicated server’s lobby membership.
                return Math.Max(1, nonServerCap + 1);
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

                // Short, valid default profile. Server can override with -profile Server
                var profileCli = args.GetStr("-profile", null);
                var defaultProfile = Application.isEditor ? "Editor" : $"Cli_{installId.Substring(0, 10)}";
                var profile = SanitizeProfile(string.IsNullOrWhiteSpace(profileCli) ? defaultProfile : profileCli.Trim());

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
                    return;
                }
                Debug.Log("[NetBootstrap] UGS initialized successfully.");

                // Wait for NetworkManager + UnityTransport
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

                SanitizeNetworkPrefabs(nm);

                // CLI
                bool wantHost = args.HasFlag("-mpsHost");
                bool allowClientAutoJoin = args.HasFlag("-autoJoin");

                string serverTypeStr = args.GetStr("-serverType", "lobby").ToLowerInvariant();
                var type = serverTypeStr == "1v1" ? ServerType.OneVOne : serverTypeStr == "2v2" ? ServerType.TwoVTwo : ServerType.Lobby;

                int max = args.GetInt("-max", type == ServerType.Lobby ? 16 : type == ServerType.OneVOne ? 2 : 4);
                int threshold = args.GetInt("-threshold", type == ServerType.Lobby ? max / 2 : max);
                SessionContext.Configure(type, max, threshold);

                if (wantHost)
                {
                    string region = args.GetStr("-region", "auto");
                    try
                    {
                        // Direct UTP host
                        // Resolve bind/port from CLI or env
                        string bind = args.GetStr("-bind", Environment.GetEnvironmentVariable("LAN_HOST") ?? "0.0.0.0");
                        int port = args.GetInt("-port", int.TryParse(Environment.GetEnvironmentVariable("LAN_PORT"), out var lp) ? lp : 7777);

                        // Configure UTP to listen
                        utp.SetConnectionData(bind, (ushort)port, bind);

                        if (!nm.StartServer())
                        {
                            Debug.LogError("[NetBootstrap] StartServer failed.");
                            return;
                        }

                        // Build lobby metadata for discovery only
                        string publicHost = args.GetStr("-publicHost", Environment.GetEnvironmentVariable("PUBLIC_HOST") ?? "127.0.0.1");
                        int publicPort = args.GetInt("-publicPort", int.TryParse(Environment.GetEnvironmentVariable("PUBLIC_PORT"), out var pp) ? pp : port);
                        // Prefer explicit -lanHost or LAN_HOST. If not set and bind is 0.0.0.0/loopback, auto-detect a real IPv4.
                        string lanHost = args.GetStr("-lanHost", Environment.GetEnvironmentVariable("LAN_HOST") ?? bind);
                        if (lanHost == "0.0.0.0" || lanHost == "127.0.0.1")
                        {
                            var auto = ResolveLocalIPv4();
                            if (!string.IsNullOrWhiteSpace(auto)) lanHost = auto;
                        }
                        int lanPort = args.GetInt("-lanPort", int.TryParse(Environment.GetEnvironmentVariable("LAN_PORT"), out var lp2) ? lp2 : port);

                        var lobbyName = $"{type}_{Guid.NewGuid():N}".Substring(0, 15);
                        var lobbyOptions = new CreateLobbyOptions
                        {
                            IsPrivate = false,
                            Data = new Dictionary<string, DataObject>
                            {
                                ["PublicHost"]  = new DataObject(DataObject.VisibilityOptions.Public, publicHost),
                                ["PublicPort"]  = new DataObject(DataObject.VisibilityOptions.Public, publicPort.ToString()),
                                ["LanEndpoint"] = new DataObject(DataObject.VisibilityOptions.Public, $"{lanHost}:{lanPort}"),
                                ["ServerType"]  = new DataObject(DataObject.VisibilityOptions.Public, type.ToString(), DataObject.IndexOptions.S1),
                                ["Scene"]       = new DataObject(DataObject.VisibilityOptions.Public, SceneManager.GetActiveScene().name),
                                ["Region"]      = new DataObject(DataObject.VisibilityOptions.Public, region, DataObject.IndexOptions.S2),
                                ["Build"]       = new DataObject(DataObject.VisibilityOptions.Public, Application.version)
                            }
                        };

                        // Reserve one seat for the dedicated server itself.
                        int playerCap   = max;
                        int lobbyCap    = ComputeLobbyCapacity(type.ToString().ToLowerInvariant(), playerCap);
                        var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, lobbyCap, lobbyOptions);

                        SessionContext.SetSession(lobby.Id, "");
                        SessionContext.SetLobby(lobby);
                        Debug.Log("[Bootstrap] FixedUpdate=" + Time.fixedDeltaTime + " vSyncCount=" + QualitySettings.vSyncCount);
                        Debug.Log($"[DirectNet] Hosting {type}. LobbyId={lobby.Id} {publicHost}:{publicPort} LAN {lanHost}:{lanPort} Region={region}");

                        // Adds [Bootstrap] tag and reports timing.

                        StartCoroutine(LobbyHeartbeat(lobby.Id));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[NetBootstrap] Failed to create DirectNet/Lobby: {e.Message}");
                        return;
                    }

                    var sceneName = args.GetStr("-scene", string.Empty);
                    if (!string.IsNullOrWhiteSpace(sceneName)) StartCoroutine(CoLoadSceneNextFrame(sceneName));
                }
                // Optional direct connect for dev testing: -connect host:port
                else
                {
                    var ep = args.GetStr("-connect", null);
                    if (!string.IsNullOrWhiteSpace(ep))
                    {
                        if (!allowClientAutoJoin)
                        {
                            Debug.Log("[NetBootstrap] UI-join mode: ignoring -connect (use -autoJoin to enable CLI connect).");
                            return;
                        }

                        try
                        {
                            var parts = ep.Split(':');
                            var host = parts[0].Trim();
                            var cport = (parts.Length > 1 && int.TryParse(parts[1], out var pv)) ? pv : 7777;

                            utp.SetConnectionData(host, (ushort)cport);

                            if (!nm.StartClient())
                            {
                                Debug.LogError("[NetBootstrap] StartClient failed.");
                                return;
                            }

                            Debug.Log($"[DirectNet] Connecting to {host}:{cport}");
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[NetBootstrap] Failed to direct-connect: {e.Message}");
                        }
                    }
                    else
                    {
#if UNITY_EDITOR
                        Debug.Log("[NetBootstrap] No host flags. Idle; UI will decide.");
#endif
                    }
                }
            }

            private static System.Collections.IEnumerator LobbyHeartbeat(string lobbyId)
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(15f);
                    _ = LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
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

                        bool remove = false;

                        // 1) Drop any entries whose root lacks a NetworkObject (e.g. cinematic stand-ins).
                        if (prefab is GameObject go && !go.TryGetComponent<Unity.Netcode.NetworkObject>(out _))
                        {
                            Debug.LogWarning($"[NetBootstrap] Removing non-network prefab from registration: {prefab.name}");
                            remove = true;
                        }

                        // 2) Drop duplicates.
                        if (!remove && !seen.Add(prefab))
                        {
                            Debug.LogWarning($"[NetBootstrap] Removing duplicate NetworkPrefab entry: {prefab.name}");
                            remove = true;
                        }

                        if (remove) list.RemoveAt(i);
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
