// Assets/Scripts/Networking/Runtime/UI/MainMenuClientUI.cs
// Purpose: Low-rate Lobby polling, Quick Join, shared throttle, and cache to avoid rate limits

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport.Relay;

namespace Game.Net
{
    public sealed class MainMenuClientUI : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Button playButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text openLobbiesText;

        [Header("Refresh")]
        [SerializeField, Min(1f)] private float lobbyRefreshSeconds = 10f; // safer default

        [Header("Guards")]
        [SerializeField] private bool singleJoinPerRun = true;
        private static bool s_DidJoinThisRun;

        // --- Shared throttle and cache across all instances ---
        private static double s_NextAllowedQueryTime;            // token-bucket style min interval
        // Adaptable global throttle (shared across scene reloads)
        private static double s_MinQueryIntervalSeconds = 3.5;   // safer base
        private static List<Lobby> s_LobbyCache;
        private static double s_LobbyCacheAt;
        private const double CacheTtlSeconds = 5.0;
        private static bool s_QueryInFlight;
        // dynamic penalty window when 429s are observed
        private static double s_ScanPenaltyUntil;
        private static int s_BackoffExp; // 0..5

        private Coroutine _ellipsisCo;
        private string _ellipsisBase;
        private bool _joining;
        private bool _servicesInitialized;
        private Lobby _tempJoinedLobby;

        // jitter
        private static readonly System.Random s_Rng = new System.Random();
        private static float Jitter(float baseSeconds, float pct = 0.25f)
        {
            var f = 1f + (float)(s_Rng.NextDouble() * pct);
            return baseSeconds * f;
        }

        private void OnEnable()
        {
            if (playButton) playButton.onClick.AddListener(OnPlayClicked);
            SetStatus("Initializing...");
            StartCoroutine(InitializeAndRefreshLoop());

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        private void OnDisable()
        {
            if (playButton) playButton.onClick.RemoveListener(OnPlayClicked);
            StopAllCoroutines();
            _ellipsisCo = null;
            _ellipsisBase = null;
            _joining = false;
        }

        private IEnumerator InitializeAndRefreshLoop()
        {
            SetStatusAnimated("Initializing services");
            while (!UgsInitializer.IsReady)
            {
                if (!string.IsNullOrEmpty(UgsInitializer.LastError))
                {
                    Debug.LogError($"[MainMenu] UGS init failed: {UgsInitializer.LastError}");
                    StopStatusAnimation();
                    SetStatus("Service init failed");
                    yield break;
                }
                yield return null;
            }

            StopStatusAnimation();
            _servicesInitialized = true;

            // Clean up any stale lobby memberships from prior runs
            yield return StartCoroutine(LeaveAllJoinedLobbies());

            // Start a single refresh loop. Do not restart it on Play.
            StartCoroutine(UpdateLobbyListLoop());
            SetStatus("Ready");
        }

        public void OnPlayClicked()
        {
            if (_joining) return;
            if (singleJoinPerRun && s_DidJoinThisRun) { SetStatus("Already joined this run."); return; }
            if (!_servicesInitialized) { SetStatus("Services not ready."); return; }

            // Do not StopAllCoroutines(); keep the one refresh loop. It auto-pauses when _joining is true.
            StartCoroutine(JoinLobbyFlow());
        }

        private IEnumerator ThrottleLobbyRead()
        {
            // serialize reads and enforce min interval
            while (s_QueryInFlight) yield return null;
            s_QueryInFlight = true;

            var now = Time.unscaledTimeAsDouble;

            // honor global penalty window if active
            if (now < s_ScanPenaltyUntil)
                yield return new WaitForSecondsRealtime((float)(s_ScanPenaltyUntil - now));

            var wait = s_NextAllowedQueryTime - now;
            if (wait > 0) yield return new WaitForSecondsRealtime((float)wait);

            // reserve next slot with slight jitter
            float jitter = UnityEngine.Random.Range(0.05f, 0.25f);
            s_NextAllowedQueryTime = Math.Max(now, s_NextAllowedQueryTime) + s_MinQueryIntervalSeconds + jitter;
        }

        private void ReleaseLobbyReadSlot()
        {
            s_QueryInFlight = false;
        }

        private IEnumerator JoinLobbyFlow()
        {
            var nm = NetworkManager.Singleton;
            if (nm && nm.IsClient && nm.IsConnectedClient)
            {
                SetStatus("Already connected.");
                yield break;
            }

            _joining = true;
            SetBusy(true);
            SetStatusAnimated("Finding server");

            // Ensure we are not stuck as a member of a previous lobby
            yield return StartCoroutine(LeaveAllJoinedLobbies());

            // First try Quick Join to cut a round trip
            Lobby joinedLobby = null;
            int quickRetries = 3;
            for (int i = 0; i < quickRetries; i++)
            {
                yield return ThrottleLobbyRead();
                var quickTask = LobbyService.Instance.QuickJoinLobbyAsync(new QuickJoinLobbyOptions
                {
                    Filter = new List<QueryFilter>
                    {
                        new QueryFilter(QueryFilter.FieldOptions.S1, "Lobby", QueryFilter.OpOptions.EQ),
                        new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
                    }
                });
                yield return new WaitUntil(() => quickTask.IsCompleted);
                ReleaseLobbyReadSlot();

                if (quickTask.Exception == null)
                {
                    joinedLobby = quickTask.Result;
                    // success: reset penalty
                    s_BackoffExp = 0; s_MinQueryIntervalSeconds = 3.5; s_ScanPenaltyUntil = 0;
                    break;
                }

                var ex = quickTask.Exception.InnerException ?? quickTask.Exception;
                if (ex is LobbyServiceException le && le.Reason == LobbyExceptionReason.RateLimited)
                {
                    s_BackoffExp = Mathf.Clamp(s_BackoffExp + 1, 0, 5);
                    double penalty = Math.Pow(2, s_BackoffExp) * 1.0;
                    s_ScanPenaltyUntil = Time.unscaledTimeAsDouble + penalty;
                    s_MinQueryIntervalSeconds = Math.Min(8.0, 3.5 + s_BackoffExp * 0.75);
                    var backoff = Jitter((float)penalty);
                    Debug.LogWarning($"[MainMenu] Rate limited on QuickJoin. Retry in {backoff:0.0}s");
                    yield return new WaitForSecondsRealtime(backoff);
                    continue;
                }
                Debug.LogWarning($"[MainMenu] QuickJoin failed: {ex.Message}");
                break;
            }

            // If Quick Join failed, fall back to cached list or a single query
            if (joinedLobby == null)
            {
                yield return StartCoroutine(GetLobbyListCachedOrQuery());
                var list = s_LobbyCache;
                if (list is List<Lobby> l && l.Count > 0)
                {
                    yield return StartCoroutine(JoinLobbyById(l[0].Id));
                    joinedLobby = _tempJoinedLobby;
                }
            }

            if (joinedLobby == null)
            {
                StopStatusAnimation();
                SetStatus("No server available");
                Done(false);
                yield break;
            }

            // Relay join
            if (!joinedLobby.Data.TryGetValue("RelayJoinCode", out var relayCodeData))
            {
                Debug.LogError("[MainMenu] No relay code in lobby");
                SetStatus("Invalid lobby");
                Done(false);
                yield break;
            }

            var joinAllocTask = RelayService.Instance.JoinAllocationAsync(relayCodeData.Value);
            yield return new WaitUntil(() => joinAllocTask.IsCompleted);
            if (joinAllocTask.Exception != null)
            {
                Debug.LogError($"[MainMenu] Relay join failed: {joinAllocTask.Exception}");
                SetStatus("Join failed");
                Done(false);
                yield break;
            }

            var utp = nm.GetComponent<UnityTransport>();
            var alloc = joinAllocTask.Result;

            // prefer DTLS; fall back if needed
            var ep = alloc.ServerEndpoints.FirstOrDefault(e => e.ConnectionType.Equals("dtls", StringComparison.OrdinalIgnoreCase))
                     ?? alloc.ServerEndpoints.FirstOrDefault();
            if (ep == null)
            {
                Debug.LogError("[MainMenu] No Relay endpoint");
                SetStatus("Join failed");
                Done(false);
                yield break;
            }

            var rsd = new RelayServerData(
                ep.Host,
                (ushort)ep.Port,
                alloc.AllocationIdBytes,
                alloc.ConnectionData,
                alloc.HostConnectionData,
                alloc.Key,
                ep.Secure || ep.ConnectionType.Equals("dtls", StringComparison.OrdinalIgnoreCase));

            utp.SetRelayServerData(rsd);

            if (!nm.StartClient())
            {
                Debug.LogError("[MainMenu] StartClient failed");
                SetStatus("Client start failed");
                Done(false);
                yield break;
            }

            float timeout = 10f;
            while (!nm.IsConnectedClient && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (nm.IsConnectedClient)
            {
                StopStatusAnimation();
                SetStatus($"Joined {joinedLobby.Name}");
                SessionContext.SetLobby(joinedLobby);
                Done(true);
            }
            else
            {
                SetStatus("Connection timeout");
                nm.Shutdown();
                Done(false);
            }
        }

        private IEnumerator GetLobbyListCachedOrQuery()
        {
            // serve cache if fresh
            if (Time.unscaledTimeAsDouble - s_LobbyCacheAt <= CacheTtlSeconds && s_LobbyCache != null)
            {
                yield return s_LobbyCache;
                yield break;
            }

            // throttle and query once
            yield return ThrottleLobbyRead();
            var queryOptions = new QueryLobbiesOptions
            {
                Count = 10,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, "Lobby", QueryFilter.OpOptions.EQ)
                },
                Order = new List<QueryOrder> { new QueryOrder(false, QueryOrder.FieldOptions.AvailableSlots) }
            };
            var task = LobbyService.Instance.QueryLobbiesAsync(queryOptions);
            yield return new WaitUntil(() => task.IsCompleted);
            ReleaseLobbyReadSlot();

            if (task.Exception != null)
            {
                var ex = task.Exception.InnerException ?? task.Exception;
                if (ex is LobbyServiceException le && le.Reason == LobbyExceptionReason.RateLimited)
                {
                    var backoff = Jitter(2f);
                    Debug.LogWarning($"[MainMenu] Rate limit on list. Backoff {backoff:0.0}s");
                    yield return new WaitForSecondsRealtime(backoff);
                    yield return new List<Lobby>(); // empty on limit
                    yield break;
                }

                Debug.LogWarning($"[MainMenu] Query failed: {ex.Message}");
                yield return new List<Lobby>();
                yield break;
            }

            s_LobbyCache = task.Result.Results ?? new List<Lobby>();
            s_LobbyCacheAt = Time.unscaledTimeAsDouble;
            yield return s_LobbyCache;
        }

        private IEnumerator JoinLobbyById(string lobbyId)
        {
            yield return ThrottleLobbyRead();
            var joinTask = LobbyService.Instance.JoinLobbyByIdAsync(lobbyId);
            yield return new WaitUntil(() => joinTask.IsCompleted);
            ReleaseLobbyReadSlot();

            if (joinTask.Exception != null)
            {
                var ex = joinTask.Exception.InnerException ?? joinTask.Exception;
                if (ex is LobbyServiceException le && le.Reason == LobbyExceptionReason.RateLimited)
                {
                    var backoff = Jitter(1.5f);
                    Debug.LogWarning($"[MainMenu] Rate limit on JoinLobby. Backoff {backoff:0.0}s");
                    yield return new WaitForSecondsRealtime(backoff);
                    yield return null;
                    yield break;
                }

                Debug.LogWarning($"[MainMenu] JoinLobby failed: {ex.Message}");
                yield return null;
                yield break;
            }

            _tempJoinedLobby = joinTask.Result;
            yield return _tempJoinedLobby;
        }

        private IEnumerator UpdateLobbyListLoop()
        {
            while (isActiveAndEnabled)
            {
                if (_servicesInitialized && !_joining)
                {
                    yield return UpdateLobbyCountOnce();
                }
                yield return new WaitForSecondsRealtime(lobbyRefreshSeconds);
            }
        }

        private IEnumerator UpdateLobbyCountOnce()
        {
            if (!AuthenticationService.Instance.IsSignedIn) yield break;

            // serve cache if fresh
            if (Time.unscaledTimeAsDouble - s_LobbyCacheAt <= CacheTtlSeconds && s_LobbyCache != null)
            {
                SetOpenCountText(s_LobbyCache);
                yield break;
            }

            yield return ThrottleLobbyRead();
            var queryOptions = new QueryLobbiesOptions
            {
                Count = 8,
                Filters = new List<QueryFilter> { new QueryFilter(QueryFilter.FieldOptions.S1, "Lobby", QueryFilter.OpOptions.EQ) }
            };

            var task = LobbyService.Instance.QueryLobbiesAsync(queryOptions);
            yield return new WaitUntil(() => task.IsCompleted);
            ReleaseLobbyReadSlot();

            if (task.Exception != null)
            {
                var ex = task.Exception.InnerException ?? task.Exception;
                if (ex is LobbyServiceException le && le.Reason == LobbyExceptionReason.RateLimited)
                {
                    // Back off aggressively and widen spacing for a short window.
                    s_BackoffExp = Mathf.Clamp(s_BackoffExp + 1, 0, 5);
                    double penalty = Math.Pow(2, s_BackoffExp) * 2.0; // 2,4,8,16,32,64s
                    s_ScanPenaltyUntil = Time.unscaledTimeAsDouble + penalty;
                    s_MinQueryIntervalSeconds = Math.Min(8.0, 3.5 + s_BackoffExp * 0.75);
                    Debug.LogWarning($"[MainMenu] Rate limit on lobby scan. Backing off {penalty:0}s");
                    yield break; // keep last shown count
                }

                Debug.LogWarning($"[MainMenu] Failed to query lobbies: {ex.Message}");
                yield break;
            }

            // Success resets penalty and tightens spacing back to base
            s_BackoffExp = 0;
            s_MinQueryIntervalSeconds = 3.5;

            s_LobbyCache = task.Result.Results ?? new List<Lobby>();
            s_LobbyCacheAt = Time.unscaledTimeAsDouble;
            SetOpenCountText(s_LobbyCache);
        }

        private void SetOpenCountText(List<Lobby> lobbies)
        {
            int openCount = lobbies.Count(l => l.AvailableSlots > 0);
            if (openLobbiesText) openLobbiesText.text = $"Open Lobbies ({openCount})";
        }

        private void SetBusy(bool busy) { if (playButton) playButton.interactable = !busy; }
        private void SetStatus(string s) { if (statusText) statusText.text = s; }
        private void SetStatusAnimated(string baseText)
        {
            StopStatusAnimation();
            _ellipsisBase = baseText;
            _ellipsisCo = StartCoroutine(Ellipsis());
        }
        private void StopStatusAnimation()
        {
            if (_ellipsisCo != null) StopCoroutine(_ellipsisCo);
            _ellipsisCo = null;
            _ellipsisBase = null;
        }

        private IEnumerator Ellipsis()
        {
            int dots = 0;
            while (!string.IsNullOrEmpty(_ellipsisBase))
            {
                dots = (dots + 1) % 4;
                SetStatus(_ellipsisBase + new string('.', dots));
                yield return new WaitForSecondsRealtime(0.33f);
            }
        }

        private void Done(bool success)
        {
            if (success && singleJoinPerRun) s_DidJoinThisRun = true;
            _joining = false;
            SetBusy(false);
        }
        
        // Best-effort leave of any joined lobbies to avoid "already a member" errors after crashes/editor restarts.
        private IEnumerator LeaveAllJoinedLobbies()
        {
            List<string> lobbyIds = null;

            // Throttle too, since these are service calls
            yield return ThrottleLobbyRead();
            var getTask = LobbyService.Instance.GetJoinedLobbiesAsync();
            yield return new WaitUntil(() => getTask.IsCompleted);
            ReleaseLobbyReadSlot();

            if (getTask.Exception == null) lobbyIds = getTask.Result;

            if (lobbyIds != null && lobbyIds.Count > 0)
            {
                for (int i = 0; i < lobbyIds.Count; i++)
                {
                    var leaveTask = LobbyService.Instance.RemovePlayerAsync(lobbyIds[i], AuthenticationService.Instance.PlayerId);
                    yield return new WaitUntil(() => leaveTask.IsCompleted);
                }
                // small cooldown so service reflects the leave
                yield return new WaitForSecondsRealtime(0.15f);
            }
        }

        private static bool ValidateNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (!nm) return false;
            if (!nm.TryGetComponent<UnityTransport>(out var utp)) return false;
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            if (nm.NetworkConfig.NetworkTransport == null) nm.NetworkConfig.NetworkTransport = utp;
            return nm.NetworkConfig.NetworkTransport != null;
        }
    }
}
