// Assets/Scripts/Networking/Runtime/UI/LobbyUI.cs
// Play panel zoom-in from 3D trigger, pause/unpause player, status panel,
// and reopen cooldown (5s) unless player leaves the platform.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;

namespace Game.Net
{
    public sealed class LobbyUI : NetworkBehaviour
    {
        [Header("Play Panel (UI)")]
        [SerializeField] private RectTransform playPanel;
        [SerializeField] private CanvasGroup playPanelCg;
        [SerializeField, Min(0.05f)] private float openDuration = 0.25f;

        [Header("Buttons")]
        [SerializeField] private Button queue1v1Button;
        [SerializeField] private Button queue2v2Button;
        [SerializeField] private Button closeButton;

        [Header("HUD")]
        [SerializeField] private GameObject playerHudRoot;

        [Header("Status Panel (child of Play Panel)")]
        [SerializeField] private GameObject statusPanel;
        [SerializeField] private TMP_Text statusText;
        [SerializeField, Min(0.5f)] private float statusShowSeconds = 5f;

        [Header("Open Cooldown")]
        [SerializeField, Min(0f)] private float reopenCooldownSeconds = 5f;

        bool _busy;
        Coroutine _statusCo;
        Canvas _rootCanvas;
        Vector3 _panelDefaultScale;
        Vector2 _panelDefaultPos;
        Vector2 _panelOpenStartPos;
        PlayerNetwork _localPlayer;

        float _lastCloseAt = -999f;
        bool _leftSinceClose = true;

        void Awake()
        {
            if (playPanel)
            {
                _panelDefaultScale = playPanel.localScale;
                _panelDefaultPos   = playPanel.anchoredPosition;
                playPanel.gameObject.SetActive(false);
            }
            if (statusPanel) statusPanel.SetActive(false);
        }

        void OnEnable()
        {
            if (queue1v1Button) queue1v1Button.onClick.AddListener(OnQueue1v1);
            if (queue2v2Button) queue2v2Button.onClick.AddListener(OnQueue2v2);
            if (closeButton)    closeButton.onClick.AddListener(ClosePlayPanel);
        }

        void OnDisable()
        {
            if (queue1v1Button) queue1v1Button.onClick.RemoveListener(OnQueue1v1);
            if (queue2v2Button) queue2v2Button.onClick.RemoveListener(OnQueue2v2);
            if (closeButton)    closeButton.onClick.RemoveListener(ClosePlayPanel);
        }

        // Call from trigger OnTriggerEnter
        public void OpenPlayPanelFromWorld(Transform worldAnchor)
        {
            // Reopen gate: must wait cooldown unless player left trigger
            if (playPanel != null && playPanel.gameObject.activeSelf) return;
            if (Time.unscaledTime - _lastCloseAt < reopenCooldownSeconds && !_leftSinceClose) return;

            if (playPanel == null) return;
            if (_rootCanvas == null) _rootCanvas = playPanel.GetComponentInParent<Canvas>(true);
            if (_rootCanvas == null) return;

            var canvasCam = _rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _rootCanvas.worldCamera;
            var screen = RectTransformUtility.WorldToScreenPoint(canvasCam, worldAnchor.position);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)playPanel.parent, screen, canvasCam, out _panelOpenStartPos);

            playPanel.gameObject.SetActive(true);
            playPanel.localScale = Vector3.zero;
            playPanel.anchoredPosition = _panelOpenStartPos;
            if (playPanelCg) playPanelCg.alpha = 0f;

            PauseLocalPlayer(true);
            if (playerHudRoot) playerHudRoot.SetActive(false);

            _busy = false;
            SetButtonsInteractable(false);
            StartCoroutine(AnimateOpen(_panelOpenStartPos, _panelDefaultPos, Vector3.zero, _panelDefaultScale));
        }

        // Call from trigger OnTriggerExit
        public void NotifyPlatformExited()
        {
            _leftSinceClose = true;
        }

        public void ClosePlayPanel()
        {
            if (playPanel == null || !playPanel.gameObject.activeSelf) return;

            StopStatusImmediate();

            playPanel.gameObject.SetActive(false);
            if (playerHudRoot) playerHudRoot.SetActive(true);
            PauseLocalPlayer(false);
            _busy = false;
            SetButtonsInteractable(true);

            _lastCloseAt = Time.unscaledTime;
            _leftSinceClose = false; // must leave trigger or wait cooldown
        }

        IEnumerator AnimateOpen(Vector2 fromPos, Vector2 toPos, Vector3 fromScale, Vector3 toScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, openDuration);
                var e = Mathf.SmoothStep(0f, 1f, t);
                playPanel.anchoredPosition = Vector2.LerpUnclamped(fromPos, toPos, e);
                playPanel.localScale       = Vector3.LerpUnclamped(fromScale, toScale, e);
                if (playPanelCg) playPanelCg.alpha = e;
                yield return null;
            }
            playPanel.anchoredPosition = toPos;
            playPanel.localScale = toScale;
            if (playPanelCg) playPanelCg.alpha = 1f;

            if (statusPanel) statusPanel.SetActive(false);
            SetButtonsInteractable(true);
        }

        void SetButtonsInteractable(bool on)
        {
            if (queue1v1Button) queue1v1Button.interactable = on && !_busy;
            if (queue2v2Button) queue2v2Button.interactable = on && !_busy;
            if (closeButton)    closeButton.interactable    = on;
        }

        void OnQueue1v1()
        {
            if (!IsClient || IsServer || _busy) return;
            _busy = true; SetButtonsInteractable(false);
            ShowStatus("Joining 1v1");
            RequestMatchServerRpc((int)MatchType.OneVOne);
        }

        void OnQueue2v2()
        {
            if (!IsClient || IsServer || _busy) return;
            _busy = true; SetButtonsInteractable(false);
            ShowStatus("Joining 2v2");
            RequestMatchServerRpc((int)MatchType.TwoVTwo);
        }

        enum MatchType : int { OneVOne = 0, TwoVTwo = 1 }

        [ServerRpc(RequireOwnership = false)]
        void RequestMatchServerRpc(int type, ServerRpcParams rpc = default)
        {
            var key = type == (int)MatchType.OneVOne ? "1v1" : "2v2";
            var open = FindOpenMatch(key);

            var target = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { rpc.Receive.SenderClientId } }
            };

            SendJoinCodeClientRpc(open?.code ?? "", target);
        }

        static SessionDirectory.Entry FindOpenMatch(string key)
        {
            var list = SessionDirectory.GetSnapshot(e => e.type == key);
            SessionDirectory.Entry best = null;
            foreach (var e in list)
            {
                if (e.current >= e.threshold) continue;
                if (best == null || e.current < best.current) best = e;
            }
            return best;
        }

        [ClientRpc]
        void SendJoinCodeClientRpc(string code, ClientRpcParams clientRpcParams = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                _busy = false; SetButtonsInteractable(true);
                ShowStatus("No servers available");
                return;
            }

            ShowStatus($"Joining {code}");
            StartCoroutine(JoinMatchRoutine(code));
        }

        IEnumerator JoinMatchRoutine(string code)
        {
            var init = UgsInitializer.EnsureAsync();
            while (!init.IsCompleted) yield return null;

            var task = MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim());
            while (!task.IsCompleted) yield return null;

            if (task.Exception != null)
            {
                _busy = false; SetButtonsInteractable(true);
                ShowStatus("Join failed");
            }
        }

        void ShowStatus(string msgBase)
        {
            if (playPanel == null || !playPanel.gameObject.activeSelf) return;
            StopStatusImmediate();
            _statusCo = StartCoroutine(StatusRoutine(msgBase, statusShowSeconds));
        }

        IEnumerator StatusRoutine(string msgBase, float seconds)
        {
            if (statusPanel) statusPanel.SetActive(true);

            float t = 0f;
            int dots = 0;
            while (t < seconds && playPanel.gameObject.activeSelf)
            {
                t += Time.unscaledDeltaTime;
                dots = (int)((t / 0.35f) % 4);
                if (statusText) statusText.text = msgBase + new string('.', dots);
                yield return null;
            }

            if (statusPanel) statusPanel.SetActive(false);
            _statusCo = null;
        }

        void StopStatusImmediate()
        {
            if (_statusCo != null) StopCoroutine(_statusCo);
            _statusCo = null;
            if (statusPanel) statusPanel.SetActive(false);
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
    }
}
