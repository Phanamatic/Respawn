// Assets/Scripts/Networking/Runtime/NetBootstrap.cs
// Unity 6 (6000.0.52f1) – Relay + Lobby host/client bootstrap with profile sanitization.
// Requires: Assets/Scripts/Networking/Runtime/Relay/RelayUtils.cs

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
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Networking.Transport.Relay;

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

            private static string SanitizeProfile(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "Default";
                var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
                if (safe.Length > 30) safe = safe.Substring(0, 30);
                return string.IsNullOrWhiteSpace(safe) ? "Default" : safe;
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
                string cliJoinCode = args.GetStr("-mpsJoin", null);
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
                        // Relay host
                        var allocation = await RelayService.Instance.CreateAllocationAsync(max);
                        var relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                        // Configure UTP for Relay
                        var rsd = RelayUtils.ToServerData(allocation, useWss: false);
                        utp.SetRelayServerData(rsd);

                        // Create Lobby and index ServerType on S1 so QuickJoin/queries work
                        var lobbyName = $"{type}_{Guid.NewGuid():N}".Substring(0, 15);
                        var lobbyOptions = new CreateLobbyOptions
                        {
                            IsPrivate = false,
                            Data = new Dictionary<string, DataObject>
                            {
                                ["RelayJoinCode"] = new DataObject(DataObject.VisibilityOptions.Public, relayJoinCode),
                                ["ServerType"]    = new DataObject(DataObject.VisibilityOptions.Public, type.ToString(), DataObject.IndexOptions.S1),
                                ["Scene"]         = new DataObject(DataObject.VisibilityOptions.Public, SceneManager.GetActiveScene().name),
                                ["Region"]        = new DataObject(DataObject.VisibilityOptions.Public, region, DataObject.IndexOptions.S2)
                            }
                        };

                        var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, max, lobbyOptions);

                        if (!nm.StartServer())
                        {
                            Debug.LogError("[NetBootstrap] StartServer failed.");
                            return;
                        }

                        SessionContext.SetSession(lobby.Id, relayJoinCode);
                        SessionContext.SetLobby(lobby);
                        Debug.Log($"[Relay] Hosting {type}. LobbyId={lobby.Id} JoinCode={relayJoinCode} Region={region}");

                        StartCoroutine(LobbyHeartbeat(lobby.Id));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[NetBootstrap] Failed to create Relay/Lobby: {e.Message}");
                        return;
                    }

                    var sceneName = args.GetStr("-scene", string.Empty);
                    if (!string.IsNullOrWhiteSpace(sceneName)) StartCoroutine(CoLoadSceneNextFrame(sceneName));
                }
                else if (!string.IsNullOrEmpty(cliJoinCode))
                {
                    if (!allowClientAutoJoin)
                    {
                        Debug.Log("[NetBootstrap] UI-join mode: ignoring -mpsJoin (use -autoJoin to enable CLI join).");
                        return;
                    }

                    try
                    {
                        var joinAllocation = await RelayService.Instance.JoinAllocationAsync(cliJoinCode);
                        var rsd = RelayUtils.ToServerData(joinAllocation, useWss: false);
                        utp.SetRelayServerData(rsd);

                        if (!nm.StartClient())
                        {
                            Debug.LogError("[NetBootstrap] StartClient failed.");
                            return;
                        }

                        Debug.Log($"[Relay] Joining with code: {cliJoinCode}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[NetBootstrap] Failed to join relay: {e.Message}");
                    }
                }
                else
                {
#if UNITY_EDITOR
                    Debug.Log("[NetBootstrap] No -mpsHost or -mpsJoin. Idle; UI will decide.");
#endif
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
