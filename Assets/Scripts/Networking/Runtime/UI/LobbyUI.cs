// Assets/Scripts/Networking/Runtime/UI/LobbyUI.cs
// Attaches to: Lobby UI canvas GameObject (as NetworkBehaviour)
// Updated to use Unity Lobby/Relay for global 1v1/2v2 matchmaking

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport.Relay;

namespace Game.Net
{
    public sealed class LobbyUI : NetworkBehaviour
    {
        [Header("Play Panel (UI)")]
        [SerializeField] private RectTransform playPanel;
        [SerializeField] private CanvasGroup playPanelCg;
        [SerializeField, Min(0.05f)] private float openDuration = 0.25f;

        [Header("Play Buttons")]
        [SerializeField] private Button queue1v1Button;
        [SerializeField] private Button queue2v2Button;
        [SerializeField] private Button playCloseButton;

        [Header("HUD")]
        [SerializeField] private GameObject playerHudRoot;

        [Header("Play Status (child of Play Panel)")]
        [SerializeField] private GameObject playStatusPanel;
        [SerializeField] private TMP_Text playStatusText;
        [SerializeField, Min(0.5f)] private float playStatusShowSeconds = 5f;

        [Header("Stats Panel (UI)")]
        [SerializeField] private RectTransform statsPanel;
        [SerializeField] private CanvasGroup statsPanelCg;
        [SerializeField] private Button statsCloseButton;
        [Header("Stats Status (optional)")]
        [SerializeField] private GameObject statsStatusPanel;
        [SerializeField] private TMP_Text statsStatusText;
        [SerializeField, Min(0.5f)] private float statsStatusShowSeconds = 5f;

        [Header("Armoury Panel (UI)")]
        [SerializeField] private RectTransform armouryPanel;
        [SerializeField] private CanvasGroup armouryPanelCg;
        [SerializeField] private Button armouryCloseButton;
        [SerializeField] private Game.Net.LoadoutUI loadoutUI; // assign on the Armoury panel
        [Header("Armoury Status (optional)")]
        [SerializeField] private GameObject armouryStatusPanel;
        [SerializeField] private TMP_Text armouryStatusText;
        [SerializeField, Min(0.5f)] private float armouryStatusShowSeconds = 5f;

        [Header("Open Cooldown")]
        [SerializeField, Min(0f)] private float reopenCooldownSeconds = 5f;

        // runtime
        bool _busy;
        Coroutine _playStatusCo, _statsStatusCo, _armouryStatusCo;
        Canvas _rootCanvas;

        Vector3 _playDefaultScale, _statsDefaultScale, _armouryDefaultScale;
        Vector2 _playDefaultPos, _statsDefaultPos, _armouryDefaultPos;
        Vector2 _playOpenStartPos, _statsOpenStartPos, _armouryOpenStartPos;

        PlayerNetwork _localPlayer;

        float _playLastCloseAt = -999f, _statsLastCloseAt = -999f, _armouryLastCloseAt = -999f;
        bool _playLeftSinceClose = true, _statsLeftSinceClose = true, _armouryLeftSinceClose = true;

        void Awake()
        {
            if (playPanel)
            {
                _playDefaultScale = playPanel.localScale;
                _playDefaultPos   = playPanel.anchoredPosition;
                playPanel.gameObject.SetActive(false);
            }
            if (playStatusPanel) playStatusPanel.SetActive(false);

            if (statsPanel)
            {
                _statsDefaultScale = statsPanel.localScale;
                _statsDefaultPos   = statsPanel.anchoredPosition;
                statsPanel.gameObject.SetActive(false);
            }
            if (statsStatusPanel) statsStatusPanel.SetActive(false);

            if (armouryPanel)
            {
                _armouryDefaultScale = armouryPanel.localScale;
                _armouryDefaultPos   = armouryPanel.anchoredPosition;
                armouryPanel.gameObject.SetActive(false);
            }
            if (armouryStatusPanel) armouryStatusPanel.SetActive(false);
        }

        void OnEnable()
        {
            if (queue1v1Button)   queue1v1Button.onClick.AddListener(QueueFor1v1);
            if (queue2v2Button)   queue2v2Button.onClick.AddListener(QueueFor2v2);
            if (playCloseButton)  playCloseButton.onClick.AddListener(ClosePlayPanel);

            if (statsCloseButton)   statsCloseButton.onClick.AddListener(CloseStatsPanel);
            if (armouryCloseButton) armouryCloseButton.onClick.AddListener(CloseArmouryPanel);
        }

        void OnDisable()
        {
            if (queue1v1Button)   queue1v1Button.onClick.RemoveListener(QueueFor1v1);
            if (queue2v2Button)   queue2v2Button.onClick.RemoveListener(QueueFor2v2);
            if (playCloseButton)  playCloseButton.onClick.RemoveListener(ClosePlayPanel);

            if (statsCloseButton)   statsCloseButton.onClick.RemoveListener(CloseStatsPanel);
            if (armouryCloseButton) armouryCloseButton.onClick.RemoveListener(CloseArmouryPanel);
        }

        // ---------- World-driven panel open ----------
        public void OpenPanelFromWorld(LobbyPanel which, Transform worldAnchor)
        {
            if (_rootCanvas == null)
            {
                var any = playPanel ? playPanel : statsPanel ? statsPanel : armouryPanel;
                if (any) _rootCanvas = any.GetComponentInParent<Canvas>(true);
            }
            if (_rootCanvas == null) return;

            if (IsAnyPanelOpen() && !IsPanelOpen(which)) return;

            var cam = _rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _rootCanvas.worldCamera;
            var screen = RectTransformUtility.WorldToScreenPoint(cam, worldAnchor.position);

            switch (which)
            {
                case LobbyPanel.Play:
                    if (!GateOpen(ref _playLastCloseAt, ref _playLeftSinceClose, playPanel)) return;
                    if (!playPanel) return;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)playPanel.parent, screen, cam, out _playOpenStartPos);
                    SetupAndAnimateOpen(playPanel, playPanelCg, _playOpenStartPos, _playDefaultPos, Vector3.zero, _playDefaultScale);
                    _busy = false; SetAllButtonsInteractable(false);
                    break;

                case LobbyPanel.Stats:
                    if (!GateOpen(ref _statsLastCloseAt, ref _statsLeftSinceClose, statsPanel)) return;
                    if (!statsPanel) return;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)statsPanel.parent, screen, cam, out _statsOpenStartPos);
                    SetupAndAnimateOpen(statsPanel, statsPanelCg, _statsOpenStartPos, _statsDefaultPos, Vector3.zero, _statsDefaultScale);
                    SetAllButtonsInteractable(false);
                    break;

                case LobbyPanel.Armoury:
                    if (!GateOpen(ref _armouryLastCloseAt, ref _armouryLeftSinceClose, armouryPanel)) return;
                    if (!armouryPanel) return;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)armouryPanel.parent, screen, cam, out _armouryOpenStartPos);
                    SetupAndAnimateOpen(armouryPanel, armouryPanelCg, _armouryOpenStartPos, _armouryDefaultPos, Vector3.zero, _armouryDefaultScale);
                    SetAllButtonsInteractable(false);
                    break;
            }
        }

        public void NotifyPlatformExited(LobbyPanel which)
        {
            switch (which)
            {
                case LobbyPanel.Play:     _playLeftSinceClose = true; break;
                case LobbyPanel.Stats:    _statsLeftSinceClose = true; break;
                case LobbyPanel.Armoury:  _armouryLeftSinceClose = true; break;
            }
        }

        public void OpenPlayPanelFromWorld(Transform worldAnchor) => OpenPanelFromWorld(LobbyPanel.Play, worldAnchor);
        public void NotifyPlatformExited() => NotifyPlatformExited(LobbyPanel.Play);

        public void ClosePlayPanel()
        {
            if (!playPanel || !playPanel.gameObject.activeSelf) return;
            StopPlayStatusImmediate();
            TeardownAfterClose();
            playPanel.gameObject.SetActive(false);
            _busy = false; SetAllButtonsInteractable(true);
            _playLastCloseAt = Time.unscaledTime; _playLeftSinceClose = false;
        }

        public void CloseStatsPanel()
        {
            if (!statsPanel || !statsPanel.gameObject.activeSelf) return;
            StopStatsStatusImmediate();
            TeardownAfterClose();
            statsPanel.gameObject.SetActive(false);
            SetAllButtonsInteractable(true);
            _statsLastCloseAt = Time.unscaledTime; _statsLeftSinceClose = false;
        }

        public void CloseArmouryPanel()
        {
            if (!armouryPanel || !armouryPanel.gameObject.activeSelf) return;
            StopArmouryStatusImmediate();
            TeardownAfterClose();
            armouryPanel.gameObject.SetActive(false);
            SetAllButtonsInteractable(true);
            _armouryLastCloseAt = Time.unscaledTime; _armouryLeftSinceClose = false;
        }

        // ---------- Play actions (Unity Relay/Lobby matchmaking) ----------
        private void QueueFor1v1() => StartCoroutine(JoinMatch("OneVOne"));
        private void QueueFor2v2() => StartCoroutine(JoinMatch("TwoVTwo"));

        private IEnumerator JoinMatch(string serverType)
        {
            if (_busy) yield break;
            _busy = true;
            SetAllButtonsInteractable(false);

            // Ensure authenticated
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                SetPlayStatus("Not signed in. Please restart.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            SetPlayStatus($"Finding {(serverType == "OneVOne" ? "1v1" : "2v2")} match...");

            // Query Unity Lobby service for matches
            List<Lobby> availableMatches = null;

            var queryOptions = new QueryLobbiesOptions
            {
                Count = 25,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, serverType, QueryFilter.OpOptions.EQ)
                },
                Order = new List<QueryOrder>
                {
                    new QueryOrder(false, QueryOrder.FieldOptions.AvailableSlots) // Most available slots first
                }
            };

            var queryTask = LobbyService.Instance.QueryLobbiesAsync(queryOptions);
            yield return new WaitUntil(() => queryTask.IsCompleted);

            if (queryTask.Exception != null)
            {
                Debug.LogError($"[LobbyUI] Failed to query matches: {queryTask.Exception}");
                SetPlayStatus("Failed to find matches.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            availableMatches = queryTask.Result.Results;

            if (availableMatches == null || availableMatches.Count == 0)
            {
                SetPlayStatus($"No {(serverType == "OneVOne" ? "1v1" : "2v2")} matches available.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            // Pick best match (most slots = waiting for players)
            var bestMatch = availableMatches[0];
            SetPlayStatus($"Joining {bestMatch.Name}...");

            var nm = NetworkManager.Singleton;
            var utp = nm.GetComponent<UnityTransport>();

            // Disconnect from current server if connected
            if (nm.IsConnectedClient)
            {
                nm.Shutdown();
                while (nm.IsConnectedClient) yield return null;
            }

            // Do NOT join the Lobby as a client. Use public data from the query result.
            if (bestMatch.Data == null || !bestMatch.Data.TryGetValue("RelayJoinCode", out var relayCodeData))
            {
                Debug.LogError("[LobbyUI] No relay code on queried lobby (public data missing)");
                SetPlayStatus("Invalid match data.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            string relayJoinCode = relayCodeData.Value;

            // Join the Relay allocation
            var joinAllocTask = RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            yield return new WaitUntil(() => joinAllocTask.IsCompleted);

            if (joinAllocTask.Exception != null)
            {
                Debug.LogError($"[LobbyUI] Failed to join allocation: {joinAllocTask.Exception}");
                SetPlayStatus("Failed to join match.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            var joinAllocation = joinAllocTask.Result;

            // Configure transport for Relay
            utp.SetRelayServerData(Game.Net.RelayUtils.ToServerData(joinAllocation, useWss: false));

            // Start client
            if (!nm.StartClient())
            {
                SetPlayStatus($"Failed to connect to {bestMatch.Name}.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            // Wait for connection or timeout
            float timeout = 10f;
            while (!nm.IsConnectedClient && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (nm.IsConnectedClient)
            {
                SetPlayStatus($"Joined {bestMatch.Name}.");
                SessionContext.SetSession(bestMatch.Id, relayJoinCode);
            }
            else
            {
                SetPlayStatus($"Connection timeout to {bestMatch.Name}.");
                nm.Shutdown();
            }

            _busy = false;
            SetAllButtonsInteractable(true);
        }

        // ---------- Status helpers ----------
        private void SetPlayStatus(string msg)
        {
            if (playStatusText) playStatusText.text = msg;
            if (playStatusPanel)
            {
                playStatusPanel.SetActive(true);
                if (_playStatusCo != null) StopCoroutine(_playStatusCo);
                _playStatusCo = StartCoroutine(HidePlayStatusAfterDelay());
            }
        }

        private IEnumerator HidePlayStatusAfterDelay()
        {
            yield return new WaitForSecondsRealtime(playStatusShowSeconds);
            if (playStatusPanel) playStatusPanel.SetActive(false);
            _playStatusCo = null;
        }

        void ShowPlayStatus(string msgBase)
        {
            if (!playPanel || !playPanel.gameObject.activeSelf) return;
            StopPlayStatusImmediate();
            _playStatusCo = StartCoroutine(StatusRoutine(playStatusPanel, playStatusText, msgBase, playStatusShowSeconds));
        }

        void StopPlayStatusImmediate()
        {
            if (_playStatusCo != null) StopCoroutine(_playStatusCo);
            _playStatusCo = null;
            if (playStatusPanel) playStatusPanel.SetActive(false);
        }

        public void ShowStatsStatus(string msgBase, float? seconds = null)
        {
            if (!statsPanel || !statsPanel.gameObject.activeSelf) return;
            StopStatsStatusImmediate();
            _statsStatusCo = StartCoroutine(StatusRoutine(statsStatusPanel, statsStatusText, msgBase, seconds ?? statsStatusShowSeconds));
        }

        void StopStatsStatusImmediate()
        {
            if (_statsStatusCo != null) StopCoroutine(_statsStatusCo);
            _statsStatusCo = null;
            if (statsStatusPanel) statsStatusPanel.SetActive(false);
        }

        public void ShowArmouryStatus(string msgBase, float? seconds = null)
        {
            if (!armouryPanel || !armouryPanel.gameObject.activeSelf) return;
            StopArmouryStatusImmediate();
            _armouryStatusCo = StartCoroutine(StatusRoutine(armouryStatusPanel, armouryStatusText, msgBase, seconds ?? armouryStatusShowSeconds));
        }

        void StopArmouryStatusImmediate()
        {
            if (_armouryStatusCo != null) StopCoroutine(_armouryStatusCo);
            _armouryStatusCo = null;
            if (armouryStatusPanel) armouryStatusPanel.SetActive(false);
        }

        IEnumerator StatusRoutine(GameObject panel, TMP_Text text, string msgBase, float seconds)
        {
            if (panel) panel.SetActive(true);

            float t = 0f;
            int dots = 0;
            while (t < seconds && panel && panel.activeSelf)
            {
                t += Time.unscaledDeltaTime;
                dots = (int)((t / 0.35f) % 4);
                if (text) text.text = msgBase + new string('.', dots);
                yield return null;
            }

            if (panel) panel.SetActive(false);
        }

        void SetupAndAnimateOpen(RectTransform rt, CanvasGroup cg, Vector2 fromPos, Vector2 toPos, Vector3 fromScale, Vector3 toScale)
        {
            rt.gameObject.SetActive(true);
            rt.localScale = fromScale;
            rt.anchoredPosition = fromPos;
            if (cg) cg.alpha = 0f;

            PauseLocalPlayer(true);
            if (playerHudRoot) playerHudRoot.SetActive(false);

            // If we opened the Armoury, refresh/load the loadout UI
            if (rt == armouryPanel && loadoutUI) loadoutUI.Opened();

            StartCoroutine(AnimateOpen(rt, cg, fromPos, toPos, fromScale, toScale));
        }

        IEnumerator AnimateOpen(RectTransform rt, CanvasGroup cg, Vector2 fromPos, Vector2 toPos, Vector3 fromScale, Vector3 toScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, openDuration);
                var e = Mathf.SmoothStep(0f, 1f, t);
                rt.anchoredPosition = Vector2.LerpUnclamped(fromPos, toPos, e);
                rt.localScale       = Vector3.LerpUnclamped(fromScale, toScale, e);
                if (cg) cg.alpha    = e;
                yield return null;
            }
            rt.anchoredPosition = toPos;
            rt.localScale = toScale;
            if (cg) cg.alpha = 1f;

            SetAllButtonsInteractable(true);
        }

        void TeardownAfterClose()
        {
            if (playerHudRoot) playerHudRoot.SetActive(true);
            PauseLocalPlayer(false);
        }

        bool GateOpen(ref float lastCloseAt, ref bool leftSinceClose, RectTransform panel)
        {
            if (panel != null && panel.gameObject.activeSelf) return false;
            if (Time.unscaledTime - lastCloseAt < reopenCooldownSeconds && !leftSinceClose) return false;
            return true;
        }

        bool IsAnyPanelOpen()
        {
            return (playPanel && playPanel.gameObject.activeSelf)
                || (statsPanel && statsPanel.gameObject.activeSelf)
                || (armouryPanel && armouryPanel.gameObject.activeSelf);
        }

        bool IsPanelOpen(LobbyPanel which)
        {
            return which switch
            {
                LobbyPanel.Play    => playPanel && playPanel.gameObject.activeSelf,
                LobbyPanel.Stats   => statsPanel && statsPanel.gameObject.activeSelf,
                LobbyPanel.Armoury => armouryPanel && armouryPanel.gameObject.activeSelf,
                _ => false
            };
        }

        void SetAllButtonsInteractable(bool on)
        {
            if (queue1v1Button)   queue1v1Button.interactable = on && !_busy;
            if (queue2v2Button)   queue2v2Button.interactable = on && !_busy;
            if (playCloseButton)  playCloseButton.interactable = on;
            if (statsCloseButton) statsCloseButton.interactable = on;
            if (armouryCloseButton) armouryCloseButton.interactable = on;
        }

        void PauseLocalPlayer(bool pause)
        {
            if (_localPlayer == null)
            {
                var nm = NetworkManager.Singleton;
                var po = nm ? nm.LocalClient?.PlayerObject : null;
                if (po) _localPlayer = po.GetComponent<PlayerNetwork>();
            }
            if (_localPlayer != null) _localPlayer.SetInputPaused(pause);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            openDuration = Mathf.Max(0.05f, openDuration);
            reopenCooldownSeconds = Mathf.Max(0f, reopenCooldownSeconds);
            playStatusShowSeconds = Mathf.Max(0.5f, playStatusShowSeconds);
            statsStatusShowSeconds = Mathf.Max(0.5f, statsStatusShowSeconds);
            armouryStatusShowSeconds = Mathf.Max(0.5f, armouryStatusShowSeconds);
        }
#endif
    }
}