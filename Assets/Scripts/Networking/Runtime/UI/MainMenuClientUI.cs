// Assets/Scripts/Networking/Runtime/UI/MainMenuClientUI.cs
// Join-on-click UI with retry + exponential backoff.

using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

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
        [SerializeField, Min(0.25f)] private float joinBaseDelaySeconds = 1.0f; // used
        [SerializeField, Min(1)] private int joinMaxRetries = 3;                 // used

        [Header("Guards")]
        [SerializeField] private bool singleJoinPerRun = true;
        private static bool s_DidJoinThisRun;

        private Coroutine _ellipsisCo;
        private string _ellipsisBase;
        private bool _joining;

        // constants for stable behavior
        private const float ConnectTimeoutSeconds = 8f;     // time to wait per attempt
        private const float MaxBackoffSeconds = 30f;        // cap

        private void OnEnable()
        {
            if (playButton) playButton.onClick.AddListener(OnPlayClicked);
            SetStatus("Idle");
            StartCoroutine(UpdateLobbyDirectoryLoop());

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

            // Parse ip:port from directory code
            var nmLocal = NetworkManager.Singleton;
            var utp = nmLocal.GetComponent<UnityTransport>();
            var parts = code.Split(':');
            if (parts.Length != 2 || !ushort.TryParse(parts[1], out ushort port))
            {
                StopStatusAnimation();
                SetStatus("Invalid join code format.");
                Done(false);
                yield break;
            }
            utp.SetConnectionData(parts[0], port);

            float backoff = Mathf.Clamp(joinBaseDelaySeconds, 0.25f, MaxBackoffSeconds);
            int attempts = Mathf.Max(1, joinMaxRetries);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                SetStatusAnimated($"Joining {friendlyName} (try {attempt}/{attempts})");
                Debug.Log($"[MainMenu] Attempting to join {code} attempt {attempt}/{attempts}");

                bool started = nmLocal.StartClient();
                if (!started)
                {
                    StopStatusAnimation();
                    SetStatus("Failed to start client.");
                }
                else
                {
                    // Wait for connected or timeout while client is active
                    float t = 0f;
                    while (t < ConnectTimeoutSeconds && nmLocal.IsClient && !nmLocal.IsConnectedClient)
                    {
                        t += Time.unscaledDeltaTime;
                        yield return null;
                    }

                    if (nmLocal.IsConnectedClient)
                    {
                        StopStatusAnimation();
                        SetStatus($"Joined {friendlyName}");
                        Done(true);
                        s_DidJoinThisRun = true;
                        yield break;
                    }
                }

                // Clean up failed attempt
                if (nmLocal.IsClient || nmLocal.ShutdownInProgress)
                {
                    nmLocal.Shutdown();
                    // give NGO a frame to teardown sockets
                    yield return null;
                }

                if (attempt < attempts)
                {
                    StopStatusAnimation();
                    SetStatus($"Retrying in {backoff:0.0}s");
                    yield return new WaitForSecondsRealtime(backoff);
                    backoff = Mathf.Min(backoff * 2f, MaxBackoffSeconds);
                }
            }

            StopStatusAnimation();
            SetStatus("Connection failed.");
            Done(false);
        }

        private static SessionDirectory.Entry PickLeastLoadedWithIndex(List<SessionDirectory.Entry> list, out int index)
        {
            index = 0;
            if (list == null || list.Count == 0) return null;

            var best = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].current < best.current)
                {
                    best = list[i];
                    index = i;
                }
            }
            return best;
        }

        private IEnumerator UpdateLobbyDirectoryLoop()
        {
            for (;;)
            {
                var lobbies = SessionDirectory.GetSnapshot(e => e.type == "lobby");
                int open = lobbies?.Count ?? 0;
                if (openLobbiesText) openLobbiesText.text = $"Open Lobbies: {open}";
                yield return new WaitForSecondsRealtime(directoryRefreshSeconds);
            }
        }

        private void SetBusy(bool busy)
        {
            if (playButton) playButton.interactable = !busy;
        }

        private void Done(bool success)
        {
            _joining = false;
            SetBusy(false);
            if (success) s_DidJoinThisRun = true;
        }

        private void SetStatus(string msg)
        {
            StopStatusAnimation();
            if (statusText) statusText.text = msg ?? "";
        }

        private void SetStatusAnimated(string msg)
        {
            StopStatusAnimation();
            _ellipsisBase = msg ?? "";
            _ellipsisCo = StartCoroutine(EllipsisCo());
        }

        private void StopStatusAnimation()
        {
            if (_ellipsisCo != null) StopCoroutine(_ellipsisCo);
            _ellipsisCo = null;
            _ellipsisBase = null;
        }

        private IEnumerator EllipsisCo()
        {
            const string kDots = "...";
            int dots = 0;
            while (true)
            {
                if (statusText) statusText.text = _ellipsisBase + kDots.Substring(0, dots);
                dots = (dots + 1) % 4;
                yield return new WaitForSecondsRealtime(0.333f);
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
