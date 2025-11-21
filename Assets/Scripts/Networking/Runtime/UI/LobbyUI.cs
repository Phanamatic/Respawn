// Assets/Scripts/Networking/Runtime/UI/LobbyUI.cs
// Attaches to: Lobby UI canvas GameObject (as NetworkBehaviour)
// Updated to use Unity Lobby for global 1v1/2v2 matchmaking with direct endpoints

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

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

        [Header("Spectate")]
        [SerializeField] private Button spectateButton;
        [SerializeField] private RectTransform spectatePanel;
        [SerializeField] private CanvasGroup spectatePanelCg;
        [SerializeField] private Button spectateCloseButton;
        [SerializeField] private RectTransform spectateListRoot;
        [SerializeField] private GameObject spectateEntryPrefab;
        [SerializeField] private TMP_Text spectateStatusText;

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
        Coroutine _spectateListCo;
        Canvas _rootCanvas;

        bool _isSpectatorAccount;

        Vector3 _playDefaultScale, _statsDefaultScale, _armouryDefaultScale;
        Vector2 _playDefaultPos, _statsDefaultPos, _armouryDefaultPos;
        Vector2 _playOpenStartPos, _statsOpenStartPos, _armouryOpenStartPos;

        PlayerNetwork _localPlayer;

        float _playLastCloseAt = -999f, _statsLastCloseAt = -999f, _armouryLastCloseAt = -999f;
        bool _playLeftSinceClose = true, _statsLeftSinceClose = true, _armouryLeftSinceClose = true;

        void Awake()
        {
            EnsureSpectateRuntimeUi();

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

            if (spectatePanel) spectatePanel.gameObject.SetActive(false);
            if (spectateButton) spectateButton.gameObject.SetActive(false);
            if (spectateStatusText) spectateStatusText.text = string.Empty;
        }

        void EnsureSpectateRuntimeUi()
        {
            // Allows shipping scenes without manually wiring spectate fields; we create a minimal panel + button at runtime.
            var root = playPanel ? playPanel.parent as RectTransform : GetComponentInParent<Canvas>(true)?.transform as RectTransform;
            if (!root) return;

            if (!spectateButton)
            {
                var go = new GameObject("SpectateButton", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(root, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot     = new Vector2(0f, 0f);
                rt.sizeDelta = new Vector2(180f, 48f);
                rt.anchoredPosition = new Vector2(24f, 24f);

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(go.transform, false);
                var labelRt = (RectTransform)labelGo.transform;
                labelRt.anchorMin = Vector2.zero; labelRt.anchorMax = Vector2.one; labelRt.offsetMin = Vector2.zero; labelRt.offsetMax = Vector2.zero;
                var tmp = labelGo.AddComponent<TextMeshProUGUI>();
                tmp.text = "Spectate Matches";
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 22f;

                spectateButton = go.GetComponent<Button>();
            }

            if (!spectatePanel)
            {
                var panel = new GameObject("SpectatePanel", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
                panel.transform.SetParent(root, false);
                var rt = (RectTransform)panel.transform;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(520f, 620f);
                rt.anchoredPosition = Vector2.zero;

                var bg = panel.GetComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.75f);

                spectatePanel   = rt;
                spectatePanelCg = panel.GetComponent<CanvasGroup>();

                // Header/status text
                var statusGo = new GameObject("Status", typeof(RectTransform));
                statusGo.transform.SetParent(panel.transform, false);
                var statusRt = (RectTransform)statusGo.transform;
                statusRt.anchorMin = new Vector2(0f, 1f); statusRt.anchorMax = new Vector2(1f, 1f); statusRt.pivot = new Vector2(0.5f, 1f);
                statusRt.sizeDelta = new Vector2(0f, 48f);
                statusRt.anchoredPosition = new Vector2(0f, -8f);
                spectateStatusText = statusGo.AddComponent<TextMeshProUGUI>();
                spectateStatusText.text = "Scanning matches...";
                spectateStatusText.alignment = TextAlignmentOptions.MidlineLeft;
                spectateStatusText.margin = new Vector4(16f, 0f, 16f, 0f);
                spectateStatusText.fontSize = 22f;

                // Close button
                var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
                closeGo.transform.SetParent(panel.transform, false);
                var closeRt = (RectTransform)closeGo.transform;
                closeRt.anchorMin = new Vector2(1f, 1f); closeRt.anchorMax = new Vector2(1f, 1f); closeRt.pivot = new Vector2(1f, 1f);
                closeRt.sizeDelta = new Vector2(36f, 36f);
                closeRt.anchoredPosition = new Vector2(-12f, -12f);
                var closeLabelGo = new GameObject("X", typeof(RectTransform));
                closeLabelGo.transform.SetParent(closeGo.transform, false);
                var closeLabel = closeLabelGo.AddComponent<TextMeshProUGUI>();
                closeLabel.text = "X";
                closeLabel.fontSize = 24f;
                closeLabel.alignment = TextAlignmentOptions.Center;
                spectateCloseButton = closeGo.GetComponent<Button>();

                // Scroll area for entries
                var scrollGo = new GameObject("List", typeof(RectTransform), typeof(ScrollRect));
                scrollGo.transform.SetParent(panel.transform, false);
                var scrollRt = (RectTransform)scrollGo.transform;
                scrollRt.anchorMin = new Vector2(0f, 0f); scrollRt.anchorMax = new Vector2(1f, 1f); scrollRt.pivot = new Vector2(0.5f, 0.5f);
                scrollRt.offsetMin = new Vector2(12f, 12f); scrollRt.offsetMax = new Vector2(-12f, -64f);
                var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
                viewport.transform.SetParent(scrollGo.transform, false);
                var viewportRt = (RectTransform)viewport.transform;
                viewportRt.anchorMin = Vector2.zero; viewportRt.anchorMax = Vector2.one; viewportRt.offsetMin = Vector2.zero; viewportRt.offsetMax = Vector2.zero;
                var maskImg = viewport.GetComponent<Image>();
                maskImg.color = new Color(1f, 1f, 1f, 0.05f);
                viewport.GetComponent<Mask>().showMaskGraphic = false;

                var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                content.transform.SetParent(viewport.transform, false);
                var contentRt = (RectTransform)content.transform;
                contentRt.anchorMin = new Vector2(0f, 1f); contentRt.anchorMax = new Vector2(1f, 1f); contentRt.pivot = new Vector2(0.5f, 1f);
                contentRt.offsetMin = Vector2.zero; contentRt.offsetMax = Vector2.zero;
                var layout = content.GetComponent<VerticalLayoutGroup>();
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                layout.spacing = 6f;
                layout.padding = new RectOffset(8, 8, 8, 8);
                var fitter = content.GetComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var scroll = scrollGo.GetComponent<ScrollRect>();
                scroll.viewport = viewportRt;
                scroll.content = contentRt;

                spectateListRoot = contentRt;
            }
        }

        void OnEnable()
        {
            if (queue1v1Button)   queue1v1Button.onClick.AddListener(QueueFor1v1);
            if (queue2v2Button)   queue2v2Button.onClick.AddListener(QueueFor2v2);
            if (playCloseButton)  playCloseButton.onClick.AddListener(ClosePlayPanel);

            if (spectateButton) spectateButton.onClick.AddListener(OpenSpectatePanel);
            if (spectateCloseButton) spectateCloseButton.onClick.AddListener(CloseSpectatePanel);

            if (statsCloseButton)   statsCloseButton.onClick.AddListener(CloseStatsPanel);
            if (armouryCloseButton) armouryCloseButton.onClick.AddListener(CloseArmouryPanel);

            StartCoroutine(DetectSpectatorAccount());
        }

        void OnDisable()
        {
            if (queue1v1Button)   queue1v1Button.onClick.RemoveListener(QueueFor1v1);
            if (queue2v2Button)   queue2v2Button.onClick.RemoveListener(QueueFor2v2);
            if (playCloseButton)  playCloseButton.onClick.RemoveListener(ClosePlayPanel);

            if (spectateButton) spectateButton.onClick.RemoveListener(OpenSpectatePanel);
            if (spectateCloseButton) spectateCloseButton.onClick.RemoveListener(CloseSpectatePanel);

            if (statsCloseButton)   statsCloseButton.onClick.RemoveListener(CloseStatsPanel);
            if (armouryCloseButton) armouryCloseButton.onClick.RemoveListener(CloseArmouryPanel);

            if (_spectateListCo != null) StopCoroutine(_spectateListCo);
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

        public void OpenSpectatePanel()
        {
            if (!_isSpectatorAccount || _busy) return;
            if (!spectatePanel) return;

            spectatePanel.gameObject.SetActive(true);
            if (spectatePanelCg) spectatePanelCg.alpha = 1f;
            PauseLocalPlayer(true);
            if (_spectateListCo != null) StopCoroutine(_spectateListCo);
            _spectateListCo = StartCoroutine(RefreshSpectateList());
        }

        public void CloseSpectatePanel()
        {
            if (!spectatePanel || !spectatePanel.gameObject.activeSelf) return;
            if (_spectateListCo != null) StopCoroutine(_spectateListCo);
            spectatePanel.gameObject.SetActive(false);
            if (spectateStatusText) spectateStatusText.text = string.Empty;
            PauseLocalPlayer(false);
            _busy = false;
            SetAllButtonsInteractable(true);
        }

        IEnumerator RefreshSpectateList()
        {
            if (spectateStatusText) spectateStatusText.text = "Scanning matches...";
            ClearSpectateList();

            var matches = new List<Lobby>();
            yield return StartCoroutine(QueryMatchesForType("1v1", matches));
            yield return StartCoroutine(QueryMatchesForType("2v2", matches));

            if (matches.Count == 0)
            {
                if (spectateStatusText) spectateStatusText.text = "No active matches.";
                yield break;
            }

            if (spectateStatusText) spectateStatusText.text = $"Open matches ({matches.Count})";
            BuildSpectateEntries(matches);
        }

        IEnumerator QueryMatchesForType(string serverType, List<Lobby> output)
        {
            var opts = new QueryLobbiesOptions
            {
                Count = 25,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.S1, serverType, QueryFilter.OpOptions.EQ)
                }
            };

            var task = LobbyService.Instance.QueryLobbiesAsync(opts);
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception == null && task.Result != null && task.Result.Results != null)
            {
                foreach (var lobby in task.Result.Results)
                {
                    if (!IsActiveSpectateCandidate(lobby))
                        continue;

                    output.Add(lobby);
                }
            }
        }

        bool IsActiveSpectateCandidate(Lobby lobby)
        {
            if (lobby == null) return false;
            if (lobby.Players == null || lobby.Players.Count == 0) return false;

            string state = lobby.Data != null && lobby.Data.TryGetValue("MatchState", out var s) ? s?.Value : null;
            if (!string.IsNullOrEmpty(state))
            {
                string st = state.Trim();
                if (string.Equals(st, "Waiting", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(st, "MatchEnd", StringComparison.OrdinalIgnoreCase)) return false;
            }

            int alive = 0, total = 0;
            if (lobby.Data != null && lobby.Data.TryGetValue("Alive", out var aliveObj))
            {
                var val = aliveObj?.Value;
                var parts = string.IsNullOrEmpty(val) ? null : val.Split('/');
                if (parts != null && parts.Length == 2)
                {
                    int.TryParse(parts[0], out alive);
                    int.TryParse(parts[1], out total);
                }
            }

            if (total > 0 && alive <= 0)
                return false;

            return true;
        }

        void BuildSpectateEntries(List<Lobby> matches)
        {
            var root = spectateListRoot ? spectateListRoot : spectatePanel;
            if (!root) return;

            string GetValue(Lobby lobby, string key, string fallback)
            {
                if (lobby == null || lobby.Data == null) return fallback;
                return lobby.Data.TryGetValue(key, out var data) && data != null && !string.IsNullOrEmpty(data.Value)
                    ? data.Value
                    : fallback;
            }

            foreach (var lobby in matches)
            {
                var entry = spectateEntryPrefab ? Instantiate(spectateEntryPrefab, root) : CreateRuntimeEntry(root);
                var texts = entry.GetComponentsInChildren<TMP_Text>(true);
                TMP_Text label = texts != null && texts.Length > 0 ? texts[0] : null;
                var button = entry.GetComponentInChildren<Button>(true);

                string name     = lobby.Name;
                string scene    = GetValue(lobby, "Scene", "?");
                string type     = GetValue(lobby, "ServerType", "Match");
                string state    = GetValue(lobby, "MatchState", "Unknown");
                string round    = GetValue(lobby, "Round", "?");
                string winsA    = GetValue(lobby, "WinsA", "0");
                string winsB    = GetValue(lobby, "WinsB", "0");
                string alive    = GetValue(lobby, "Alive", "?");
                string aliveAB  = GetValue(lobby, "AliveAB", null);
                string elapsed  = GetValue(lobby, "Elapsed", "0");

                string summary = $"{name} • {type} @ {scene}\n" +
                                 $"State: {state}  Round: {round}\n" +
                                 $"Score {winsA}-{winsB}  Alive {aliveAB ?? alive}  Time {elapsed}s";

                if (label) label.text = summary;
                if (button)
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() =>
                    {
                        if (_busy) return;
                        _busy = true;
                        SetAllButtonsInteractable(false);
                        _spectateListCo = StartCoroutine(ConnectToLobbyEndpoint(lobby, true, SetSpectateStatus));
                    });
                }
            }
        }

        void ClearSpectateList()
        {
            var root = spectateListRoot ? spectateListRoot : spectatePanel;
            if (!root) return;
            for (int i = root.childCount - 1; i >= 0; i--)
                Destroy(root.GetChild(i).gameObject);
        }

        GameObject CreateRuntimeEntry(RectTransform parent)
        {
            var go = new GameObject("SpectateEntry", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 4f;
            layout.padding = new RectOffset(8, 8, 8, 8);

            var textGo = new GameObject("Label", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var label = textGo.AddComponent<TextMeshProUGUI>();
            label.fontSize = 18f;
            label.alignment = TextAlignmentOptions.TopLeft;
#if UNITY_6000_0_OR_NEWER
            label.textWrappingMode = TextWrappingModes.Normal;
#else
            label.enableWordWrapping = true;
#endif

            var btnGo = new GameObject("JoinButton", typeof(RectTransform));
            btnGo.transform.SetParent(go.transform, false);
            var btnImage = btnGo.AddComponent<Image>();
            btnImage.color = new Color(0.2f, 0.55f, 0.2f, 0.9f);
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = btnImage;
            var btnLabelGo = new GameObject("Text", typeof(RectTransform));
            btnLabelGo.transform.SetParent(btnGo.transform, false);
            var btnLabel = btnLabelGo.AddComponent<TextMeshProUGUI>();
            btnLabel.text = "Join as Spectator";
            btnLabel.alignment = TextAlignmentOptions.Center;
            btnLabel.fontSize = 16f;

            var fitter = btnGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return go;
        }

        // ---------- Play actions (Unity Lobby matchmaking with direct endpoints) ----------
    private void QueueFor1v1() => StartCoroutine(JoinMatch("1v1"));
    private void QueueFor2v2() => StartCoroutine(JoinMatch("2v2"));
    // Match the S1 values our servers publish ("1v1"/"2v2"), not "OneVOne"/"TwoVTwo".

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

            SetPlayStatus($"Finding {serverType} match...");
            // Keep label in sync with actual filter value.

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
                SetPlayStatus($"No {serverType} matches available.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            // Pick best match (most slots = waiting for players)
            var bestMatch = availableMatches[0];
            SetPlayStatus($"Joining {bestMatch.Name}...");

            yield return StartCoroutine(ConnectToLobbyEndpoint(bestMatch, false, SetPlayStatus));
        }

        IEnumerator ConnectToLobbyEndpoint(Lobby lobby, bool asSpectator, System.Action<string> statusSetter)
        {
            statusSetter ??= SetPlayStatus;

            var nm = NetworkManager.Singleton;
            var utp = nm.GetComponent<UnityTransport>();

            if (lobby.Data == null)
            {
                Debug.LogError("[LobbyUI] Missing lobby public data");
                statusSetter("Invalid match data.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            bool hasHost = lobby.Data.TryGetValue("PublicHost", out var publicHostData);
            bool hasPort = lobby.Data.TryGetValue("PublicPort", out var publicPortData);
            string lanEp = lobby.Data.TryGetValue("LanEndpoint", out var lanEpData) ? lanEpData.Value : null;

            if (!hasHost || !hasPort || string.IsNullOrWhiteSpace(publicHostData.Value))
            {
                Debug.LogError("[LobbyUI] No public endpoint on lobby");
                statusSetter("Invalid match data.");
                _busy = false;
                SetAllButtonsInteractable(true);
                yield break;
            }

            string publicHost = publicHostData.Value;
            int publicPort = int.TryParse(publicPortData.Value, out var pp) ? pp : 7777;

            SessionContext.SetDirectEndpoint(publicHost, publicPort, lanEp);
            ConnectionMetadata.SetLocalPayload(new ConnectionPayloadData
            {
                displayName = GetLocalDisplayName(),
                spectator = asSpectator
            }, nm);

            IEnumerator ReconnectAndConnect(string host, int prt, float seconds)
            {
                if (nm.IsListening || nm.IsClient || nm.IsServer)
                {
                    nm.Shutdown();
                    float t0 = 5f;
                    while ((nm.IsListening || nm.ShutdownInProgress) && t0 > 0f)
                    {
                        t0 -= Time.unscaledDeltaTime;
                        yield return null;
                    }
                    yield return null;
                }

                string target = host;
                try
                {
                    if (!System.Net.IPAddress.TryParse(host, out var ip) ||
                        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        var addrs = System.Net.Dns.GetHostAddresses(host);
                        foreach (var a in addrs)
                            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { target = a.ToString(); break; }
                    }
                }
                catch { }

                utp.SetConnectionData(target, (ushort)prt);
                if (!nm.StartClient()) yield break;

                float t = seconds;
                while (!nm.IsConnectedClient && t > 0f) { t -= Time.deltaTime; yield return null; }
            }

            bool connected = false;
            if (!string.IsNullOrWhiteSpace(lanEp) && lanEp.Contains(":"))
            {
                var parts = lanEp.Split(':');
                var lh = parts[0].Trim();
                var lp = (parts.Length > 1 && int.TryParse(parts[1], out var v)) ? v : publicPort;

                statusSetter($"Connecting (LAN) {lh}:{lp}...");
                yield return StartCoroutine(ReconnectAndConnect(lh, lp, 5f));
                connected = nm.IsConnectedClient;
            }

            if (!connected)
            {
                statusSetter($"Connecting {publicHost}:{publicPort}...");
                yield return StartCoroutine(ReconnectAndConnect(publicHost, publicPort, 10f));
            }

            if (nm.IsConnectedClient)
            {
                statusSetter(asSpectator ? $"Spectating {lobby.Name}." : $"Joined {lobby.Name}.");
                SessionContext.SetSession(lobby.Id, "");
            }
            else
            {
                statusSetter($"Connection failed to {lobby.Name}.");
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

        private void SetSpectateStatus(string msg)
        {
            if (spectateStatusText) spectateStatusText.text = msg;
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
                || (armouryPanel && armouryPanel.gameObject.activeSelf)
                || (spectatePanel && spectatePanel.gameObject.activeSelf);
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
            if (spectateButton) spectateButton.interactable = on && !_busy && _isSpectatorAccount;
            if (spectateCloseButton) spectateCloseButton.interactable = on;
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

        string GetLocalDisplayName()
        {
            var name = Game.Services.PlayerIdentityState.LocalDisplayName;
            if (string.IsNullOrWhiteSpace(name) && AuthenticationService.Instance != null)
                name = AuthenticationService.Instance.PlayerName;
            return string.IsNullOrWhiteSpace(name) ? "Spectator" : name;
        }

        IEnumerator DetectSpectatorAccount()
        {
            var ensureTask = Game.Services.PlayerIdentityState.EnsureIdentityAsync();
            while (!ensureTask.IsCompleted) yield return null;

            var name = Game.Services.PlayerIdentityState.LocalDisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = AuthenticationService.Instance != null ? AuthenticationService.Instance.PlayerName : null;

            _isSpectatorAccount = string.Equals(name, "Spectator1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Spectator2", StringComparison.OrdinalIgnoreCase);

            if (spectateButton) spectateButton.gameObject.SetActive(_isSpectatorAccount);
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