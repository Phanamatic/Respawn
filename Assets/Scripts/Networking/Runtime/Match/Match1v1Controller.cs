// Assets/Scripts/Networking/Runtime/Match/Match1v1Controller.cs
// Server-authoritative: players ride the ship, are hidden during selection, then spawn at the chosen point.
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using TMPro;

namespace Game.Net
{
    public enum MatchState : byte { Waiting = 0, Countdown = 1, FlyIn = 2, SpawnSelect = 3, Playing = 4 }

    [DefaultExecutionOrder(-9000)]
    public sealed class Match1v1Controller : NetworkBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Match1v1Areas areas;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("HUD (client-only, optional)")]
        [SerializeField] private CanvasGroup statusCanvas;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text countdownText;
        [SerializeField] private CanvasGroup spawnCanvas;
        [SerializeField] private TMP_Text spawnHintText;

        [Header("Cinematic (client-only visuals)")]
        [SerializeField] private GameObject shipPrefab;
        [SerializeField] private Transform shipStart;
        [SerializeField] private Transform shipEnd;
        [SerializeField, Min(0.1f)] private float shipDuration = 3f;
        [SerializeField, Min(1f)] private float cameraFollowLerp = 12f;
        [SerializeField] private string seatMountName = "SeatMount";
        [SerializeField] private string cameraMountName = "CameraMount";
        [SerializeField] private string cameraLookAtName = "CameraLookAt";

        [Header("Timings")]
        [SerializeField, Min(1f)] private int countdownSeconds = 3;
        [SerializeField, Min(3f)] private float cinematicSeconds = 3.5f;
        [SerializeField, Min(3f)] private float spawnSelectSeconds = 10f;

        private readonly NetworkVariable<MatchState> _state = new NetworkVariable<MatchState>(MatchState.Waiting);
        private readonly NetworkVariable<int> _playerCount = new NetworkVariable<int>(0);

        private readonly Dictionary<ulong, TeamId> _teams = new();
        private readonly Dictionary<ulong, Vector3> _chosenSpawns = new();
        private float _spawnDeadlineServer;

        // Local UI
        private bool _selecting;
        private Bounds _myAreaBounds;
        private float _spawnDeadlineLocal;
        private Coroutine _flyCo, _selectCo, _uiCo;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
                RecountPlayers();
                AssignTeamsIfNeeded();
                TryStartFlow();
            }

            _state.OnValueChanged += (_, __) => RefreshUI();
            _playerCount.OnValueChanged += (_, __) => RefreshUI();
            if (IsClient) RefreshUI();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }

        void OnClientConnected(ulong clientId)
        {
            RecountPlayers();
            AssignTeamsIfNeeded();
            PauseInputFor(clientId, true);
            TryStartFlow();
        }

        void OnClientDisconnected(ulong clientId)
        {
            _teams.Remove(clientId);
            _chosenSpawns.Remove(clientId);
            RecountPlayers();

            if (_playerCount.Value < 2)
            {
                _state.Value = MatchState.Waiting;
                BroadcastPauseAll(true);
                _chosenSpawns.Clear();
            }
        }

        void RecountPlayers()
        {
            int count = 0;
            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++) if (ids[i] != NetworkManager.ServerClientId) count++;
            _playerCount.Value = count;
        }

        void AssignTeamsIfNeeded()
        {
            if (!areas || !areas.HasAll) return;
            var ids = new List<ulong>(NetworkManager.ConnectedClientsIds);
            ids.Remove(NetworkManager.ServerClientId);
            ids.Sort();
            for (int i = 0; i < ids.Count; i++)
                if (!_teams.ContainsKey(ids[i])) _teams[ids[i]] = i % 2 == 0 ? TeamId.A : TeamId.B;
        }

        void TryStartFlow()
        {
            if (_state.Value != MatchState.Waiting) return;
            if (_playerCount.Value < 2) return;
            StartCoroutine(ServerFlow());
        }

        IEnumerator ServerFlow()
        {
            if (_state.Value != MatchState.Waiting) yield break;

            _state.Value = MatchState.Countdown;
            for (int i = countdownSeconds; i >= 1; i--)
            {
                StartCountdownClientRpc(i);
                yield return new WaitForSeconds(1f);
            }

            // Seat and freeze on ship start.
            if (IsServer) SeatPlayersAtShipStart();
            FreezeAllPlayers(true);

            _state.Value = MatchState.FlyIn;
            ClearCountdownClientRpc();
            PlayFlyInClientRpc();               // client visual
            yield return MovePlayersAlongShipPathServer(); // move players with ship on server

            _state.Value = MatchState.SpawnSelect;

            // Hide everyone during selection to avoid seeing players on map early.
            SetAllPlayersVisibleClientRpc(false);

            _chosenSpawns.Clear();
            _spawnDeadlineServer = (float)NetworkManager.ServerTime.Time + spawnSelectSeconds;

            foreach (var kv in _teams)
            {
                var bounds = kv.Value == TeamId.A ? TeamBoundsA() : TeamBoundsB();
                BeginSpawnSelectClientRpc(bounds.center, bounds.size, spawnSelectSeconds, ToClient(kv.Key));
            }

            while (_state.Value == MatchState.SpawnSelect)
            {
                bool all = true;
                foreach (var kv in _teams) if (!_chosenSpawns.ContainsKey(kv.Key)) { all = false; break; }
                if (all) break;
                if ((float)NetworkManager.ServerTime.Time >= _spawnDeadlineServer) break;
                yield return null;
            }

            foreach (var kv in _teams)
                if (!_chosenSpawns.ContainsKey(kv.Key))
                    _chosenSpawns[kv.Key] = areas.GetRandomPoint(kv.Value);

            // Teleport to chosen spawns.
            foreach (var kv in _teams)
            {
                ulong cid = kv.Key;
                if (!NetworkManager.ConnectedClients.TryGetValue(cid, out var cc) || !cc.PlayerObject) continue;

                Vector3 pos = _chosenSpawns[cid];
                pos.y = areas.transform.position.y;

                var t = cc.PlayerObject.transform;
                t.position = pos;

                var look = areas.GetNeutralCenter() - t.position; look.y = 0f;
                if (look.sqrMagnitude > 0.001f) t.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }

            // Reveal and unfreeze together.
            SetAllPlayersVisibleClientRpc(true);
            FreezeAllPlayers(false);

            foreach (var kv in _teams)
                ConfirmSpawnClientRpc(_chosenSpawns[kv.Key], ToClient(kv.Key));

            BroadcastPauseAll(false);
            _state.Value = MatchState.Playing;
        }

        // --- Ship seating and movement ---

        void SeatPlayersAtShipStart()
        {
            if (!IsServer || !shipStart) return;

            const float seatOffset = 1.5f;
            foreach (var kv in _teams)
            {
                if (!NetworkManager.ConnectedClients.TryGetValue(kv.Key, out var cc) || !cc.PlayerObject) continue;
                var t = cc.PlayerObject.transform;
                float side = kv.Value == TeamId.A ? -1f : 1f;
                Vector3 pos = shipStart.position + shipStart.right * (seatOffset * side);
                t.SetPositionAndRotation(pos, shipStart.rotation);
            }
        }

        IEnumerator MovePlayersAlongShipPathServer()
        {
            if (!IsServer || !shipStart || !shipEnd) { yield return new WaitForSeconds(cinematicSeconds); yield break; }

            const float seatOffset = 1.5f;
            float t0 = Time.time;
            float t1 = t0 + shipDuration;

            while (Time.time < t1)
            {
                float u = Mathf.InverseLerp(t0, t1, Time.time);
                float e = 1f - Mathf.Pow(1f - u, 3f); // ease-out

                Vector3 shipPos = Vector3.Lerp(shipStart.position, shipEnd.position, e);
                Quaternion shipRot = Quaternion.Slerp(shipStart.rotation, shipEnd.rotation, e);

                foreach (var kv in _teams)
                {
                    if (!NetworkManager.ConnectedClients.TryGetValue(kv.Key, out var cc) || !cc.PlayerObject) continue;
                    var t = cc.PlayerObject.transform;

                    float side = kv.Value == TeamId.A ? -1f : 1f;
                    Vector3 offset = shipRot * (Vector3.right * (seatOffset * side));
                    t.SetPositionAndRotation(shipPos + offset, shipRot);
                }

                yield return null;
            }
        }

        // --- UI and spawn-select ---

        [ClientRpc] void StartCountdownClientRpc(int n)
        {
            if (!IsClient) return;
            ShowCanvas(statusCanvas, true);
            if (statusText) statusText.text = "Starting match";
            PulseCountdown(n);
        }

        [ClientRpc] void ClearCountdownClientRpc()
        {
            if (!IsClient) return;
            ClearCountdownUI();
        }

        [ClientRpc] void PlayFlyInClientRpc()
        {
            if (!IsClient) return;
            ClearCountdownUI();
            if (_flyCo != null) StopCoroutine(_flyCo);
            _flyCo = StartCoroutine(CoFlyInClient());
        }

        IEnumerator CoFlyInClient()
        {
            if (!shipPrefab || !shipStart || !shipEnd) yield break;

            var go = Instantiate(shipPrefab, shipStart.position, shipStart.rotation);
            Transform seat     = string.IsNullOrEmpty(seatMountName)    ? null : go.transform.Find(seatMountName);
            Transform camMount = string.IsNullOrEmpty(cameraMountName)  ? null : go.transform.Find(cameraMountName);
            Transform camLook  = string.IsNullOrEmpty(cameraLookAtName) ? null : go.transform.Find(cameraLookAtName);

            var cam = Camera.main;
            var iso = cam ? cam.GetComponent<IsometricCamera>() : null;
            if (iso) iso.enabled = false;

            // Instant cut to ship view.
            if (cam)
            {
                Vector3 toPos = camMount ? camMount.position :
                                seat ? seat.position + go.transform.forward * -8f + Vector3.up * 5f :
                                shipEnd.position + shipEnd.forward * -8f + Vector3.up * 5f;

                Vector3 lookPos = camLook ? camLook.position :
                                  seat ? seat.position :
                                  shipEnd.position;

                var toRot = Quaternion.LookRotation((lookPos - toPos).normalized, Vector3.up);
                cam.transform.SetPositionAndRotation(toPos, toRot);
            }

            float t0 = Time.time, t1 = t0 + shipDuration;
            while (Time.time < t1)
            {
                float u = Mathf.InverseLerp(t0, t1, Time.time);
                float e = 1f - Mathf.Pow(1f - u, 3f);

                go.transform.SetPositionAndRotation(
                    Vector3.Lerp(shipStart.position, shipEnd.position, e),
                    Quaternion.Slerp(shipStart.rotation, shipEnd.rotation, e)
                );

                if (cam)
                {
                    Vector3 toPos =
                        camMount ? camMount.position :
                        seat ? seat.position + go.transform.forward * -8f + Vector3.up * 5f :
                        shipEnd.position + shipEnd.forward * -8f + Vector3.up * 5f;

                    Vector3 lookPos =
                        camLook ? camLook.position :
                        seat ? seat.position :
                        shipEnd.position;

                    var toRot = Quaternion.LookRotation((lookPos - toPos).normalized, Vector3.up);
                    float t = 1f - Mathf.Exp(-cameraFollowLerp * Time.unscaledDeltaTime);
                    cam.transform.position = Vector3.Lerp(cam.transform.position, toPos, t);
                    cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, toRot, t);
                }
                yield return null;
            }

            if (iso) iso.enabled = true;
            Destroy(go);
        }

        [ClientRpc]
        void BeginSpawnSelectClientRpc(Vector3 areaCenter, Vector3 areaSize, float seconds, ClientRpcParams p = default)
        {
            if (!IsClient) return;
            _myAreaBounds = new Bounds(areaCenter, areaSize);
            _selecting = true;
            _spawnDeadlineLocal = Time.unscaledTime + seconds;

            ShowCanvas(spawnCanvas, true);
            UpdateSpawnHint();

            SpawnAreaHighlighter.SetMode(SpawnAreaHighlighter.Mode.Choosing, _myAreaBounds);

            if (_selectCo != null) StopCoroutine(_selectCo);
            _selectCo = StartCoroutine(CoSpawnSelect());
        }

        IEnumerator CoSpawnSelect()
        {
            while (_selecting)
            {
                UpdateSpawnHint();

                var cam = Camera.main;
                if (cam && Input.GetMouseButtonDown(0))
                {
                    if (Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit, 500f, groundMask, QueryTriggerInteraction.Ignore))
                    {
                        var p = hit.point;
                        if (_myAreaBounds.Contains(p))
                        {
                            SubmitSpawnChoiceServerRpc(p);
                            _selecting = false;
                            if (spawnHintText) spawnHintText.text = "Spawning...";
                        }
                    }
                }

                if (Time.unscaledTime >= _spawnDeadlineLocal) _selecting = false;
                yield return null;
            }
        }

        void UpdateSpawnHint()
        {
            float remain = Mathf.Max(0f, _spawnDeadlineLocal - Time.unscaledTime);
            if (spawnHintText) spawnHintText.text = $"Click in your area to spawn\nAuto-assign in {Mathf.CeilToInt(remain)}s";
        }

        [ClientRpc]
        void ConfirmSpawnClientRpc(Vector3 pos, ClientRpcParams p = default)
        {
            if (!IsClient) return;
            _selecting = false;
            ShowCanvas(spawnCanvas, false);
            ShowCanvas(statusCanvas, false);
            ClearCountdownUI();
            SpawnAreaHighlighter.SetMode(SpawnAreaHighlighter.Mode.Hidden, default);
        }

        [ServerRpc(RequireOwnership = false)]
        void SubmitSpawnChoiceServerRpc(Vector3 worldPoint, ServerRpcParams rpc = default)
        {
            if (!IsServer || _state.Value != MatchState.SpawnSelect) return;

            ulong cid = rpc.Receive.SenderClientId;
            if (!_teams.TryGetValue(cid, out var team)) return;
            if (!areas || !areas.HasAll) return;
            if (!areas.Contains(team, worldPoint)) return;

            _chosenSpawns[cid] = worldPoint;
        }

        void FreezeAllPlayers(bool frozen)
        {
            foreach (var cc in NetworkManager.ConnectedClientsList)
            {
                if (!cc.PlayerObject) continue;
                var pn = cc.PlayerObject.GetComponent<Game.Net.PlayerNetwork>();
                if (pn) pn.SetFrozenServer(frozen);
            }
        }

        [ClientRpc]
        void SetAllPlayersVisibleClientRpc(bool visible)
        {
            if (!IsClient) return;
            var list = FindObjectsByType<Game.Net.PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < list.Length; i++) if (list[i]) list[i].SetVisible(visible);
        }

        void PauseInputFor(ulong clientId, bool paused) =>
            SetPlayerInputPausedClientRpc(paused, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });

        void BroadcastPauseAll(bool paused)
        {
            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
            {
                var cid = ids[i];
                if (cid == NetworkManager.ServerClientId) continue;
                PauseInputFor(cid, paused);
            }
        }

        [ClientRpc] void SetPlayerInputPausedClientRpc(bool paused, ClientRpcParams p = default)
        {
            if (!IsClient) return;
            var players = FindObjectsByType<Game.Net.PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                var pl = players[i];
                if (pl && pl.IsOwner) { pl.SetInputPaused(paused); break; }
            }

            if (paused)
            {
                ShowCanvas(statusCanvas, true);
                if (statusText) statusText.text = _state.Value == MatchState.Waiting
                    ? $"Waiting for {_playerCount.Value}/2 players"
                    : "Please wait";
            }
        }

        Bounds TeamBoundsA() => areas.GetTeamCollider(TeamId.A).bounds;
        Bounds TeamBoundsB() => areas.GetTeamCollider(TeamId.B).bounds;

        void RefreshUI()
        {
            if (!IsClient) return;

            if (_state.Value == MatchState.Waiting)
            {
                ShowCanvas(statusCanvas, true);
                if (statusText) statusText.text = $"Waiting for {_playerCount.Value}/2 players";
                ShowCanvas(spawnCanvas, false);
                SetCountdownText("");
            }
            else if (_state.Value == MatchState.Playing)
            {
                ShowCanvas(statusCanvas, false);
                ShowCanvas(spawnCanvas, false);
                SetCountdownText("");
            }
        }

        void PulseCountdown(int number)
        {
            SetCountdownText(number.ToString());
            if (_uiCo != null) StopCoroutine(_uiCo);
            _uiCo = StartCoroutine(CoPulse(countdownText));
        }

        IEnumerator CoPulse(TMP_Text t)
        {
            if (!t) yield break;
            var rt = t.rectTransform;
            float d = 0.6f;
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime < t0 + d)
            {
                float u = Mathf.InverseLerp(t0, t0 + d, Time.unscaledTime);
                float s = 1.0f + 0.35f * Mathf.Sin(u * Mathf.PI);
                rt.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            rt.localScale = Vector3.one;
        }

        void ClearCountdownUI()
        {
            if (_uiCo != null) { StopCoroutine(_uiCo); _uiCo = null; }
            SetCountdownText("");
        }

        void SetCountdownText(string s)
        {
            if (!countdownText) return;
            countdownText.text = s ?? "";
            countdownText.rectTransform.localScale = Vector3.one;
        }

        static void ShowCanvas(CanvasGroup cg, bool on)
        {
            if (!cg) return;
            cg.alpha = on ? 1f : 0f;
            cg.interactable = on;
            cg.blocksRaycasts = on;
            if (cg.gameObject.activeSelf != on) cg.gameObject.SetActive(on);
        }
    }
}
