// Assets/Scripts/Networking/Runtime/UI/MainMenuClientUI.cs
// Join-on-click UI with robust session handling.

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Multiplayer;

namespace Game.Net
{
    public sealed class MainMenuClientUI : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Button playButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text openLobbiesText;

        [Header("Refresh")]
        [SerializeField, Min(0.25f)] private float directoryRefreshSeconds = 1.0f;

        [Header("Join Backoff")]
        [SerializeField, Min(0.25f)] private float joinBaseDelaySeconds = 1.0f;
        [SerializeField, Min(1)] private int joinMaxRetries = 3;

        [Header("Guards")]
        [SerializeField] private bool singleJoinPerRun = true;
        private static bool s_DidJoinThisRun;

        private Coroutine _ellipsisCo;
        private string _ellipsisBase;
        private bool _joining;

        private void OnEnable()
        {
            if (playButton) playButton.onClick.AddListener(OnPlayClicked);
            SetStatus("Idle");
            StartCoroutine(UpdateLobbyDirectoryLoop());
            // Force 60 FPS as requested
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

        public void OnPlayClicked()
        {
            if (_joining) return;
            if (singleJoinPerRun && s_DidJoinThisRun) { SetStatus("Already joined this run."); return; }

            StopAllCoroutines();
            StartCoroutine(JoinLobbyFlow());
            StartCoroutine(UpdateLobbyDirectoryLoop());
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
            SetStatusAnimated("Looking for lobby");

            // Wait for UGS init (done by NetBootstrap)
            float waitInit = 0f;
            while (!UgsInitializer.IsReady && waitInit < 15f)
            {
                waitInit += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!UgsInitializer.IsReady)
            {
                StopStatusAnimation();
                SetStatus("UGS not ready.");
                Done(false);
                yield break;
            }

            var lobbies = SessionDirectory.GetSnapshot(e => e.type == "lobby");
            if (lobbies == null || lobbies.Count == 0)
            {
                StopStatusAnimation();
                SetStatus("No lobby servers available.");
                Done(false);
                yield break;
            }

            int friendlyIndex;
            var best = PickLeastLoadedWithIndex(lobbies, out friendlyIndex);
            string friendlyName = $"Lobby_{friendlyIndex}";
            string code = best.code.Trim();

            if (!ValidateNetworkManager())
            {
                StopStatusAnimation();
                SetStatus("NetworkManager/Transport missing.");
                Done(false);
                yield break;
            }

            SetStatusAnimated("Leaving old sessions");
            yield return LeaveAllSessionsAndWait(10f);
            yield return new WaitForSecondsRealtime(1.5f);

            var joined = false;
            float delay = Mathf.Max(0.25f, joinBaseDelaySeconds);
            int attempts429 = 0;
            int resumeAttempts = 0;
            const int MaxResumeAttempts = 3;

            while (!joined)
            {
                SetStatusAnimated($"Joining {friendlyName}");
                Debug.Log($"[MainMenu] Attempting to join {code}");

                var join = MultiplayerService.Instance.JoinSessionByCodeAsync(code);
                while (!join.IsCompleted) yield return null;

                if (join.Exception == null)
                {
                    StopStatusAnimation();
                    SetStatus($"Joined {friendlyName}");

                    bool ok = false;
                    yield return StartNgoClientAndWait(v => ok = v);
                    if (ok) { Done(true); yield break; }

                    SetStatus("NGO connect timeout.");
                    Done(false);
                    yield break;
                }

                if (IsAlreadyMember(join.Exception))
                {
                    StopStatusAnimation();
                    if (resumeAttempts >= MaxResumeAttempts)
                    {
                        SetStatus("Stuck in existing membership. Please restart client.");
                        Debug.LogWarning("[MainMenu] Max resume attempts reached.");
                        Done(false);
                        yield break;
                    }

                    SetStatus("Resuming existing session...");
                    Debug.LogWarning("[MainMenu] Already a member; attempting to resume NGO connection.");

                    bool ok = false;
                    yield return StartNgoClientAndWait(v => ok = v);
                    if (ok) { Done(true); yield break; }

                    SetStatus("Resyncing membership...");
                    yield return ForceLeaveAllSessionsRoutine();
                    yield return new WaitForSecondsRealtime(2f);
                    resumeAttempts++;
                    continue;
                }

                if (IsTooManyRequests(join.Exception) && attempts429 < joinMaxRetries)
                {
                    StopStatusAnimation();
                    SetStatus($"Rate limited. Retrying in {delay:0.0}s");
                    yield return new WaitForSecondsRealtime(delay);
                    attempts429++; delay *= 2f;
                    continue;
                }

                StopStatusAnimation();
                SetStatus("Join failed: " + Flatten(join.Exception));
                Done(false);
                yield break;
            }
        }

        private IEnumerator StartNgoClientAndWait(System.Action<bool> onDone)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) { onDone?.Invoke(false); yield break; }

            if (!nm.IsClient && !nm.IsServer)
            {
                if (!nm.StartClient())
                {
                    SetStatus("StartClient failed.");
                    onDone?.Invoke(false);
                    yield break;
                }
            }

            float t = 0f, timeout = 15f;
            while (!nm.IsConnectedClient && t < timeout)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            onDone?.Invoke(nm.IsConnectedClient);
        }

        // -------- Directory UI --------
        private IEnumerator UpdateLobbyDirectoryLoop()
        {
            while (isActiveAndEnabled)
            {
                var lobbies = SessionDirectory.GetSnapshot(e => e.type == "lobby");
                if (openLobbiesText)
                {
                    if (lobbies == null || lobbies.Count == 0)
                    {
                        openLobbiesText.text = "Open Lobbies (0)";
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        sb.Append("Open Lobbies (").Append(lobbies.Count).Append("): ");
                        for (int i = 0; i < lobbies.Count; i++)
                        {
                            var e = lobbies[i];
                            var label = $"Lobby_{i + 1}";
                            sb.Append(label).Append(' ')
                              .Append(e.code).Append(' ')
                              .Append(e.current).Append('/').Append(e.max);
                            if (i < lobbies.Count - 1) sb.Append("  |  ");
                        }
                        openLobbiesText.text = sb.ToString();
                    }
                }
                yield return new WaitForSecondsRealtime(Mathf.Max(0.25f, directoryRefreshSeconds));
            }
        }

        private static SessionDirectory.Entry PickLeastLoadedWithIndex(List<SessionDirectory.Entry> list, out int index1Based)
        {
            index1Based = 1;
            if (list == null || list.Count == 0) return null;

            SessionDirectory.Entry best = list[0];
            int bestIndex = 0;
            for (int i = 1; i < list.Count; i++)
            {
                var e = list[i];
                if (e.current < best.current) { best = e; bestIndex = i; }
            }
            index1Based = bestIndex + 1;
            return best;
        }

        // -------- Busy + status helpers --------
        private void SetBusy(bool busy) { if (playButton) playButton.interactable = !busy; }
        private void SetStatus(string s) { if (statusText) statusText.text = s; }
        private void SetStatusAnimated(string baseText) { StopStatusAnimation(); _ellipsisBase = baseText; _ellipsisCo = StartCoroutine(Ellipsis()); }
        private void StopStatusAnimation() { if (_ellipsisCo != null) StopCoroutine(_ellipsisCo); _ellipsisCo = null; _ellipsisBase = null; }
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

        // -------- Leave helpers --------
        private static IEnumerator LeaveAllSessionsAndWait(float timeoutSeconds)
        {
            yield return ForceLeaveAllSessionsRoutine();
            yield return new WaitForSecondsRealtime(1f);

            float elapsed = 0f;
            var poll = 0.5f;
            while (elapsed < timeoutSeconds)
            {
                bool any = false;
                var listTask = CallNoArgTask("GetPlayerSessionsAsync") ?? CallNoArgTask("ListSessionsForPlayerAsync");
                if (listTask != null)
                {
                    float taskTimeout = 5f, taskElapsed = 0f;
                    while (!listTask.IsCompleted && taskElapsed < taskTimeout) { yield return null; taskElapsed += Time.unscaledDeltaTime; }

                    if (listTask.IsCompleted && listTask.Exception == null)
                    {
                        var result = GetTaskResult(listTask);
                        if (result is System.Collections.IEnumerable enumerable)
                            foreach (var _ in enumerable) { any = true; break; }
                    }
                }
                if (!any) yield break;
                yield return new WaitForSecondsRealtime(poll);
                elapsed += poll;
            }
        }

        private static System.Threading.Tasks.Task CallNoArgTask(string method)
        {
            var mps = MultiplayerService.Instance;
            var m = mps?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
            return m != null ? (System.Threading.Tasks.Task)m.Invoke(mps, null) : null;
        }

        private static object GetTaskResult(System.Threading.Tasks.Task t)
        {
            var prop = t.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return prop != null ? prop.GetValue(t) : null;
        }

        private static IEnumerator ForceLeaveAllSessionsRoutine()
        {
            var mps = MultiplayerService.Instance;
            if (mps == null) yield break;

            var direct = mps.GetType().GetMethod("LeaveCurrentSessionAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (direct != null)
            {
                var t = direct.Invoke(mps, null) as System.Threading.Tasks.Task;
                if (t != null) { while (!t.IsCompleted) yield return null; }
            }

            var getMine = mps.GetType().GetMethod("GetPlayerSessionsAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? mps.GetType().GetMethod("ListSessionsForPlayerAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getMine != null)
            {
                var listTask = getMine.Invoke(mps, null) as System.Threading.Tasks.Task;
                if (listTask != null)
                {
                    while (!listTask.IsCompleted) yield return null;
                    var result = GetTaskResult(listTask);
                    if (result is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var s in enumerable)
                        {
                            var idProp = s.GetType().GetProperty("Id") ?? s.GetType().GetProperty("SessionId");
                            var id = idProp != null ? idProp.GetValue(s) as string : null;
                            if (string.IsNullOrEmpty(id)) continue;

                            var leaveWithId = mps.GetType().GetMethod("LeaveSessionAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)
                                               ?? mps.GetType().GetMethod("RemovePlayerFromSessionAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                            if (leaveWithId != null)
                            {
                                System.Threading.Tasks.Task t;
                                if (leaveWithId.GetParameters().Length == 1)
                                    t = leaveWithId.Invoke(mps, new object[] { id }) as System.Threading.Tasks.Task;
                                else
                                {
                                    var playerId = TryGetPlayerId();
                                    if (string.IsNullOrEmpty(playerId)) continue;
                                    t = leaveWithId.Invoke(mps, new object[] { id, playerId }) as System.Threading.Tasks.Task;
                                }
                                if (t != null) { while (!t.IsCompleted) yield return null; }
                            }
                        }
                    }
                }
            }
        }

        // -------- Misc --------
        private static bool IsAlreadyMember(System.AggregateException ex)
        {
            var s = Flatten(ex).ToLower();
            return s.Contains("already a member") || s.Contains("already in the session");
        }

        private static bool IsTooManyRequests(System.AggregateException ex)
        {
            var s = Flatten(ex).ToLower();
            return s.Contains("too many requests") || s.Contains("429");
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

        private static string TryGetPlayerId()
        {
            try
            {
                var authType = System.Type.GetType("Unity.Services.Authentication.AuthenticationService, Unity.Services.Authentication");
                var instProp = authType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                var inst = instProp?.GetValue(null);
                var pidProp = authType?.GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.Public);
                return pidProp?.GetValue(inst) as string;
            }
            catch { return null; }
        }

        private static string Flatten(System.Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" -> ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }
    }
}
