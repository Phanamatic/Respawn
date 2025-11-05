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
                // Redundant with NetworkConfigOptimizer but safe if that object is removed.
                QualitySettings.vSyncCount = 0;
                QualitySettings.maxQueuedFrames = 1;
                Application.targetFrameRate = 60;
                Application.runInBackground = true;
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

                // Headless server path: load scene, start NGO server, then publish a lobby.
                if (Application.isBatchMode || HasArg("-headless") || HasArg("-serverName"))
                {
                    StartCoroutine(HeadlessServerFlow());
                    // Do not continue into any client path below.
                    return;
                }

                // ... (original client/editor flow continues)
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

                // Pin NGO tick (server + client). 128 is your fixedDeltaTime; keep them aligned.
                nm.NetworkConfig.TickRate = 128;

                // Configure Unity Transport for direct UDP with WAN-safe payload.
                // Payload ~1200 bytes to respect typical internet MTU when headers are present.
                utp.MaxPayloadSize = 1200;
                // [DirectNet] Direct UDP C→S; MTU-safe payloads.

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

                        // Auto-port fallback (server): try requested port then walk up to MAX_PORT
                        var cmd = new System.Collections.Generic.List<string>(System.Environment.GetCommandLineArgs());

                        var listenBind = System.Environment.GetEnvironmentVariable("BIND") ?? "0.0.0.0";
                        ushort basePort = (ushort)(System.Environment.GetEnvironmentVariable("BASE_PORT") is string bp && ushort.TryParse(bp, out var bpp) ? bpp : 7777);
                        ushort maxPort  = (ushort)(System.Environment.GetEnvironmentVariable("MAX_PORT")  is string mp && ushort.TryParse(mp, out var mpp) ? mpp : 7786);
                        ushort initial  = (ushort)cmd.GetUShort("-port", basePort);

                        var transport = (Unity.Netcode.Transports.UTP.UnityTransport)NetworkManager.Singleton.NetworkConfig.NetworkTransport;

                        ushort chosen = 0;
                        for (ushort p = initial; p <= maxPort; p++)
                        {
                            // Server listen bind
                            transport.SetConnectionData(listenBind, p);   // address, port
                            if (NetworkManager.Singleton.StartServer())
                            {
                                chosen = p;
                                UnityEngine.Debug.Log($"[DirectNet] Bound {listenBind}:{chosen} (range {initial}-{maxPort})");
                                break;
                            }
                            UnityEngine.Debug.LogWarning($"[DirectNet] Port {p} busy, trying next...");
                        }

                        if (chosen == 0)
                        {
                            UnityEngine.Debug.LogError($"[NetBootstrap] StartServer failed across range {initial}-{maxPort}. No free UDP port.");
                            return;
                        }

                        // Read hosts for Lobby publish (if your advertiser uses them)
                        var publicHost = cmd.Get("-publicHost", System.Environment.GetEnvironmentVariable("PUBLIC_HOST") ?? "respawnserver.tplinkdns.com");
                        var lanHost    = cmd.Get("-lanHost",    System.Environment.GetEnvironmentVariable("LAN_HOST")    ?? "192.168.0.150");
                        // TODO: Publish PublicHost=publicHost, PublicPort=chosen, LanEndpoint=$"{lanHost}:{chosen}", Region=<ZA>.

                        // Build lobby metadata for discovery only
                        publicHost = cmd.Get("-publicHost", System.Environment.GetEnvironmentVariable("PUBLIC_HOST") ?? "127.0.0.1");
                        int publicPort = (int)cmd.GetUShort("-publicPort", chosen);
                        // Prefer explicit -lanHost or LAN_HOST. If not set and bind is 0.0.0.0/loopback, auto-detect a real IPv4.
                        lanHost = cmd.Get("-lanHost", System.Environment.GetEnvironmentVariable("LAN_HOST") ?? listenBind);
                        if (lanHost == "0.0.0.0" || lanHost == "127.0.0.1")
                        {
                            var auto = ResolveLocalIPv4();
                            if (!string.IsNullOrWhiteSpace(auto)) lanHost = auto;
                        }
                        int lanPort = (int)cmd.GetUShort("-lanPort", chosen);

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

            static bool HasArg(string flag)
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            static string GetArg(string key, string @default = "")
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], key, System.StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return @default;
            }

            private static string ResolveLocalLanIPv4()
            {
                try
                {
                    var all = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in all)
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        var ipProps = nic.GetIPProperties();
                        foreach (var ua in ipProps.UnicastAddresses)
                        {
                            var ip = ua.Address;
                            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            var s = ip.ToString();
                            if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172.16.") || s.StartsWith("172.17.") ||
                                s.StartsWith("172.18.") || s.StartsWith("172.19.") || s.StartsWith("172.2") || s.StartsWith("172.3"))
                                return s;
                        }
                    }
                }
                catch {}
                return null;
            }
            // Dev: best-effort private IPv4 pick for LAN advertisement.

            private System.Collections.IEnumerator HeadlessServerFlow()
            // Use the non-generic IEnumerator for Unity coroutines.
            {
                // 1) Decide scenes & server metadata
                string sceneName       = GetArg("-scene", "Lobby");          // gameplay scene (Lobby/Match_1v1/Match_2v2)
                string bootstrapScene  = GetArg("-bootstrapScene", "Account"); // holds the NetworkManager (DontDestroyOnLoad)
                string serverName      = GetArg("-serverName", $"{sceneName}_{GetArg("-port", "7777")}");
                string region          = GetArg("-region", System.Environment.GetEnvironmentVariable("REGION") ?? "ZA");

                string publicHost = System.Environment.GetEnvironmentVariable("PUBLIC_HOST") ?? GetArg("-publicHost", "respawnserver.tplinkdns.com");
                int    publicPort = int.TryParse(System.Environment.GetEnvironmentVariable("PUBLIC_PORT"), out var p1) ? p1 : int.Parse(GetArg("-port", "7777"));
                string lanHost    = System.Environment.GetEnvironmentVariable("LAN_HOST")    ?? GetArg("-lanHost", "192.168.0.150");
                int    lanPort    = int.TryParse(System.Environment.GetEnvironmentVariable("LAN_PORT"), out var p2) ? p2 : publicPort;

                // Resolve a real LAN IP if someone passed 0.0.0.0 (not connectable)
                if (string.IsNullOrWhiteSpace(lanHost) || lanHost == "0.0.0.0")
                    lanHost = ResolveLocalLanIPv4() ?? "192.168.0.150";

                // 2) Ensure the bootstrap (Account) scene is loaded so NetworkManager exists & persists
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != bootstrapScene)
                {
                    Debug.Log($"[Bootstrap] Loading bootstrap scene (with NetworkManager): {bootstrapScene}");
                    yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(bootstrapScene, UnityEngine.SceneManagement.LoadSceneMode.Single);
                }

                // 3) Wait for NetworkManager from the bootstrap scene
                float t0 = Time.realtimeSinceStartup;
                while (NetworkManager.Singleton == null && Time.realtimeSinceStartup - t0 < 10f)
                    yield return null;

                if (NetworkManager.Singleton == null)
                {
                    Debug.LogError("[Bootstrap] No NetworkManager found after loading bootstrap scene. Server cannot start.");
                    yield break;
                }

                // Make extra sure it survives the next scene load (in case the prefab isn't already NNDO).
                DontDestroyOnLoad(NetworkManager.Singleton.gameObject);

                // 4) Load the target gameplay scene AFTER NetworkManager exists
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != sceneName)
                {
                    Debug.Log($"[Bootstrap] Loading server gameplay scene: {sceneName}");
                    yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(sceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
                }

                // Apply UTP listen bind/port (server)
                var utp = NetworkManager.Singleton.NetworkConfig.NetworkTransport as Unity.Netcode.Transports.UTP.UnityTransport;
                if (utp != null)
                {
                    // Bind to all adapters on given port; payload MTU ~1200 WAN-safe
                    ushort port = (ushort)publicPort;
                    utp.SetConnectionData("0.0.0.0", port, "0.0.0.0");
                    utp.MaxSendQueueSize = 1024 * 1024;
                    utp.MaxPacketQueueSize = 1024;
                }

                Debug.Log("[Bootstrap] Starting NGO Server…");
                if (!NetworkManager.Singleton.StartServer())
                {
                    Debug.LogError("[Bootstrap] Failed to start NGO Server.");
                    yield break;
                }

                // Confirm we're actually running as server before advertising.
                if (!NetworkManager.Singleton.IsServer)
                {
                    Debug.LogError("[Bootstrap] Not in server mode after StartServer(). Aborting lobby advertise.");
                    yield break;
                }

                // 4) Create/advertise a Lobby with endpoint keys
                // Derive "kind" for discovery (Lobby / 1v1 / 2v2)
                // -serverType comes from QuickBuildAndRun (e.g., "lobby","1v1","2v2")
                string serverTypeArg = GetArg("-serverType", "lobby").ToLowerInvariant();
                string lobbyKind = serverTypeArg switch
                {
                    "1v1" => "1v1",
                    "2v2" => "2v2",
                    _     => "Lobby"
                };

                // Compose data directly here to avoid stale copies.
                var createOpts = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new System.Collections.Generic.Dictionary<string, Unity.Services.Lobbies.Models.DataObject>
                    {
                        ["S1"]           = new DataObject(DataObject.VisibilityOptions.Public, lobbyKind),
                        ["PublicHost"]   = new DataObject(DataObject.VisibilityOptions.Public, publicHost),
                        ["PublicPort"]   = new DataObject(DataObject.VisibilityOptions.Public, publicPort.ToString()),
                        ["LanEndpoint"]  = new DataObject(DataObject.VisibilityOptions.Public, $"{lanHost}:{lanPort}"),
                        ["Region"]       = new DataObject(DataObject.VisibilityOptions.Public, region),
                        ["Build"]        = new DataObject(DataObject.VisibilityOptions.Public, Application.version ?? "dev")
                    }
                };

                int maxPlayers = int.TryParse(GetArg("-max", "32"), out var m) ? m : 32;

                Lobby lobby = null;

                // Kick off async create and yield until it finishes (coroutine-friendly).
                var createTask = Unity.Services.Lobbies.LobbyService.Instance.CreateLobbyAsync(serverName, maxPlayers, createOpts);
                while (!createTask.IsCompleted) yield return null;

                if (createTask.Exception != null)
                {
                    var ex = createTask.Exception.InnerException ?? createTask.Exception;
                    Debug.LogError($"[DirectNet] CreateLobby failed: {ex.Message}");
                    yield break;
                }

                lobby = createTask.Result;
                Debug.Log($"[DirectNet] Hosting Lobby. LobbyId={lobby?.Id} {publicHost}:{publicPort} LAN {lanHost}:{lanPort}");

                // 5) Heartbeat while running
                if (!string.IsNullOrEmpty(lobby?.Id))
                    StartCoroutine(LobbyHeartbeatLoop(lobby.Id));
            }

            // Wrapper to use async Lobby API inside coroutine flow.
            private System.Threading.Tasks.Task<Lobby> awaitable_CreateLobby(string name, int max, CreateLobbyOptions opts)
                => Unity.Services.Lobbies.LobbyService.Instance.CreateLobbyAsync(name, max, opts);

            private System.Collections.IEnumerator LobbyHeartbeatLoop(string lobbyId)
            // Use the non-generic IEnumerator for Unity coroutines.
            {
                var wait = new WaitForSecondsRealtime(15f);
                while (!string.IsNullOrEmpty(lobbyId))
                {
                    var task = Unity.Services.Lobbies.LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                    while (!task.IsCompleted) yield return null;
                    yield return wait;
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
                // Extra banner for quick server/client side-by-side comparison.
                Debug.Log($"[UGS] Init OK. ProjectId={Application.cloudProjectId}, Env={environmentName}, Profile={profile}, PlayerId={AuthenticationService.Instance.PlayerId}");
                // Keep a short tag so it's easy to grep in logs.
                Debug.Log($"[Bootstrap] UGS: ProjectId={Application.cloudProjectId} Env={environmentName} Profile={profile}");
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Debug.LogError("[UGS] Init failed: " + ex);
            }
        }
    }
}
