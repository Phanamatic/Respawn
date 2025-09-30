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
            yield return new WaitForSecondsRealtime(1.5f);

            var joined = false;
            float delay = Mathf.Max(0.25f, joinBaseDelaySeconds);
            int attempts429 = 0;

            while (!joined)
            {
                SetStatusAnimated($"Joining {friendlyName}");
                Debug.Log($"[MainMenu] Attempting to join {code}");

                // Parse code as ip:port
                var parts = code.Split(':');
                if (parts.Length != 2 || !ushort.TryParse(parts[1], out ushort port))
                {
                    StopStatusAnimation();
                    SetStatus("Invalid join code format.");
                    Done(false);
                    yield break;
                }

                var utp = nm.GetComponent<UnityTransport>();
                if (utp == null)
                {
                    StopStatusAnimation();
                    SetStatus("UnityTransport missing.");
                    Done(false);
                    yield break;
                }

                utp.SetConnectionData(parts[0], port);

                bool ok = false;
                yield return StartNgoClientAndWait(v => ok = v);
                if (ok) 
                { 
                    StopStatusAnimation();
                    SetStatus($"Joined {friendlyName}");
                    Done(true); 
                    yield break; 
                }

                if (attempts429 < joinMaxRetries)
                {
                    StopStatusAnimation();
                    SetStatus($"Connection failed. Retrying in {delay:0.0}s");
                    yield return new WaitForSecondsRealtime(delay);
                    attempts429++; delay *= 2f;
                    continue;
                }

                StopStatusAnimation();
                SetStatus("Join failed.");
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
                        openLobbiesText.text = $"Open Lobbies ({lobbies.Count})";
                    }
                }
                yield return new WaitForSecondsRealtime(directoryRefreshSeconds);
            }
        }

        private static SessionDirectory.Entry PickLeastLoadedWithIndex(List<SessionDirectory.Entry> list, out int friendlyIndex)
        {
            friendlyIndex = 0;
            if (list == null || list.Count == 0) return null;

            list.Sort((a, b) => a.current.CompareTo(b.current));
            var best = list[0];

            if (!string.IsNullOrEmpty(best.name) && best.name.StartsWith("Lobby_"))
            {
                var tail = best.name.Substring("Lobby_".Length);
                if (int.TryParse(tail, out int idx)) friendlyIndex = idx;
            }
            if (friendlyIndex == 0) friendlyIndex = 1;
            return best;
        }

        // -------- UI helpers --------
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

        // -------- Misc --------
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