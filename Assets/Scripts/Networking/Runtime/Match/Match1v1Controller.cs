// Assets/Scripts/Networking/Runtime/Match/Match1v1Controller.cs
// Server-authoritative: players ride the ship visually, are hidden during selection, then spawn at ground-snapped points.

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
        [SerializeField] private string seatMountName = "SeatMount";
        [SerializeField] private string cameraMountName = "CameraMount";
        [SerializeField] private string cameraLookAtName = "CameraLookAt";

        [Header("Spawn Select")]
        [SerializeField] private GameObject spawnCursorPrefab;
        [SerializeField] private Vector3 spawnCameraPosition;
        [SerializeField] private Vector3 spawnCameraLookAt;

        [Header("Timings")]
        [SerializeField, Min(1f)] private int countdownSeconds = 3;
        [SerializeField, Min(3f)] private float cinematicSeconds = 3.5f;
        [SerializeField, Min(3f)] private float spawnSelectSeconds = 10f;

        private readonly NetworkVariable<MatchState> _state = new NetworkVariable<MatchState>(MatchState.Waiting);
        private readonly NetworkVariable<int> _playerCount = new NetworkVariable<int>(0);

        private readonly Dictionary<ulong, TeamId> _teams = new();
        private readonly Dictionary<ulong, Vector3> _chosenSpawns = new();
        private float _spawnDeadlineServer;

        // Local UI/state
        private bool _selecting;
        private Bounds _myAreaBounds;
        private float _spawnDeadlineLocal;
        private Coroutine _flyCo, _selectCo, _uiCo;

        // Spawn visualizer
        private GameObject _spawnCursor;

        // Camera control
        private Camera _cam;
        private IsometricCamera _isoCam;
        private Transform _originalFollow;
        private bool _inSpawnCam;

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

            if (IsClient)
            {
                _cam = Camera.main;
                if (_cam) _isoCam = _cam.GetComponent<IsometricCamera>();
            }
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
            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
            {
                var cid = ids[i];
                if (cid == NetworkManager.ServerClientId) continue;
                if (!_teams.ContainsKey(cid)) _teams[cid] = (TeamId)((_teams.Count % 2) == 0 ? 0 : 1);

                // Tell only that client its team.
                SetMyTeamClientRpc(_teams[cid], ToClient(cid));
            }
        }

        [ClientRpc]
        void SetMyTeamClientRpc(TeamId team, ClientRpcParams p = default)
        {
            var mine = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < mine.Length; i++)
                if (mine[i] && mine[i].IsOwner) { mine[i].SetTeam(team); break; }
        }

        void TryStartFlow()
        {
            if (_state.Value != MatchState.Waiting) return;
            if (_playerCount.Value < 2) return;

            StartCoroutine(CoStartFlow());
        }

        IEnumerator CoStartFlow()
        {
            _state.Value = MatchState.Countdown;

            for (int i = countdownSeconds; i > 0; i--)
            {
                CountdownClientRpc(i);
                yield return new WaitForSecondsRealtime(1f);
            }

            CountdownClientRpc(0);
            yield return new WaitForSecondsRealtime(0.25f);

            StartCinematic();
        }

        void StartCinematic()
        {
            _state.Value = MatchState.FlyIn;
            BroadcastPauseAll(true);
            FreezeAllPlayers(true);
            StartCinematicClientRpc();
            StartCoroutine(CoEndCinematicAfter(cinematicSeconds));
        }

        [ClientRpc]
        void StartCinematicClientRpc()
        {
            if (!IsClient) return;
            if (_flyCo != null) StopCoroutine(_flyCo);
            _flyCo = StartCoroutine(CoFlyIn());
        }

        IEnumerator CoFlyIn()
        {
            if (_isoCam) _isoCam.enabled = false;

            var ship = shipPrefab ? Instantiate(shipPrefab) : null;
            if (!ship || !shipStart || !shipEnd) yield break;

            var seatMount = ship.transform.Find(seatMountName);
            var cameraMount = ship.transform.Find(cameraMountName);
            var lookAt = ship.transform.Find(cameraLookAtName);
            if (!seatMount || !cameraMount || !lookAt) { Debug.LogError("Ship mounts missing."); yield break; }

            AttachAllToShip(seatMount);

            if (_cam)
            {
                _cam.transform.SetParent(cameraMount, false);
                _cam.transform.localPosition = Vector3.zero;
                _cam.transform.LookAt(lookAt.position);
            }

            float t0 = Time.unscaledTime;
            float t1 = t0 + shipDuration;
            while (Time.unscaledTime < t1)
            {
                float u = Mathf.InverseLerp(t0, t1, Time.unscaledTime);
                UpdateShipPosition(ship, u);
                yield return null;
            }

            UpdateShipPosition(ship, 1f);
            DetachAllFromShip();
            if (_isoCam) _isoCam.enabled = true;
            if (_cam) _cam.transform.SetParent(null, true);
            Destroy(ship);
        }

        void UpdateShipPosition(GameObject ship, float u)
        {
            if (!shipStart || !shipEnd) return;
            ship.transform.position = Vector3.Lerp(shipStart.position, shipEnd.position, u);
            ship.transform.rotation = Quaternion.Slerp(shipStart.rotation, shipEnd.rotation, u);
        }

        void AttachAllToShip(Transform seatMount)
        {
            var list = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < list.Length; i++)
            {
                var pl = list[i];
                if (!pl) continue;
                pl.transform.SetParent(seatMount, true);
                pl.transform.localPosition = pl.GetTeam() == TeamId.A ? Vector3.left * 1f : Vector3.right * 1f;
                pl.SetVisible(true);
            }
        }

        void DetachAllFromShip()
        {
            var list = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < list.Length; i++)
                if (list[i]) list[i].transform.SetParent(null, true);
        }

        IEnumerator CoEndCinematicAfter(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            SetAllPlayersVisibleClientRpc(false);
            StartSpawnSelect();
        }

        void StartSpawnSelect()
        {
            _state.Value = MatchState.SpawnSelect;
            _spawnDeadlineServer = Time.unscaledTime + spawnSelectSeconds;
            _chosenSpawns.Clear();

            // Target each client with their own bounds and a duration, not an absolute time.
            foreach (var kv in _teams)
            {
                var cid = kv.Key;
                var b = areas.GetTeamCollider(kv.Value).bounds;
                BeginSpawnSelectClientRpc(b.center, b.size, spawnSelectSeconds, ToClient(cid));
            }

            StartCoroutine(CoWatchSpawnDeadline());
        }

        [ClientRpc]
        void BeginSpawnSelectClientRpc(Vector3 areaCenter, Vector3 areaSize, float seconds, ClientRpcParams p = default)
        {
            if (!IsClient) return;

            _myAreaBounds = new Bounds(areaCenter, areaSize);
            _spawnDeadlineLocal = Time.unscaledTime + seconds;
            _selecting = true;

            ShowCanvas(spawnCanvas, true);
            if (spawnHintText) spawnHintText.text = "Click to choose spawn";

            SpawnAreaHighlighter.SetMode(SpawnAreaHighlighter.Mode.Choosing, _myAreaBounds);

            if (spawnCursorPrefab)
            {
                _spawnCursor = Instantiate(spawnCursorPrefab);
                _spawnCursor.SetActive(false);
            }

            if (_isoCam)
            {
                _originalFollow = _isoCam.follow;
                _isoCam.enabled = false;
            }
            if (_cam)
            {
                _cam.transform.position = spawnCameraPosition;
                _cam.transform.LookAt(spawnCameraLookAt);
            }
            _inSpawnCam = true;

            if (_selectCo != null) StopCoroutine(_selectCo);
            _selectCo = StartCoroutine(CoSpawnSelectTimer());
        }

        IEnumerator CoSpawnSelectTimer()
        {
            while (_selecting && Time.unscaledTime < _spawnDeadlineLocal)
            {
                if (spawnHintText)
                {
                    float remain = Mathf.Max(0f, _spawnDeadlineLocal - Time.unscaledTime);
                    spawnHintText.text = $"Click to choose spawn ({remain:0}s)";
                }
                yield return null;
            }
            if (_selecting) EndSpawnSelect(false);
        }

        void Update()
        {
            if (!_selecting || !_cam) return;

            var mouseRay = _cam.ScreenPointToRay(Input.mousePosition);
            bool validHit = Physics.Raycast(mouseRay, out var hit, 2000f, groundMask, QueryTriggerInteraction.Ignore)
                            && _myAreaBounds.Contains(hit.point);

            if (validHit)
            {
                if (_spawnCursor)
                {
                    _spawnCursor.transform.position = hit.point + Vector3.up * 0.01f;
                    _spawnCursor.SetActive(true);
                }

                if (Input.GetMouseButtonDown(0))
                {
                    ChooseSpawnServerRpc(hit.point);
                    EndSpawnSelect(true);
                }
            }
            else
            {
                if (_spawnCursor) _spawnCursor.SetActive(false);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        void ChooseSpawnServerRpc(Vector3 point, ServerRpcParams rpc = default)
        {
            if (!IsServer || _state.Value != MatchState.SpawnSelect) return;
            ulong cid = rpc.Receive.SenderClientId;
            if (!_teams.TryGetValue(cid, out var team)) return;
            if (!areas.Contains(team, point)) return;

            _chosenSpawns[cid] = point;
            if (_chosenSpawns.Count >= _playerCount.Value)
                SpawnAllAtChosenPoints();
        }

        IEnumerator CoWatchSpawnDeadline()
        {
            yield return new WaitForSecondsRealtime(spawnSelectSeconds + 0.1f);
            if (_state.Value == MatchState.SpawnSelect) SpawnAllAtChosenPoints();
        }

        void SpawnAllAtChosenPoints()
        {
            _state.Value = MatchState.Playing;

            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
            {
                var cid = ids[i];
                if (cid == NetworkManager.ServerClientId) continue;
                if (!_teams.TryGetValue(cid, out var team)) continue;

                Vector3 point = _chosenSpawns.TryGetValue(cid, out var chosen) ? chosen : areas.GetRandomPoint(team);
                SpawnPlayerAtServer(cid, point, team);
            }

            SetAllPlayersVisibleClientRpc(true);
            BroadcastPauseAll(false);
        }

        // inside Match1v1Controller
void SpawnPlayerAtServer(ulong clientId, Vector3 point, TeamId team)
{
    var player = NetworkManager.ConnectedClients[clientId]?.PlayerObject?.GetComponent<PlayerNetwork>();
    if (!player) return;

    // set facing first
    Vector3 look = areas.GetNeutralCenter() - point; look.y = 0f;
    Quaternion rot = look.sqrMagnitude > 0.001f ? Quaternion.LookRotation(look.normalized, Vector3.up) : Quaternion.identity;
    player.transform.SetPositionAndRotation(point, rot);

    // hard snap to ground, then unfreeze
    var capsule = player.GetComponent<CapsuleCollider>();
    GroundClampServer.SnapToGround(player.transform, groundMask, 0.02f, capsule, 10f, 50f);

    player.SetFrozenServer(false);
}


        [ClientRpc]
        void SetAllPlayersVisibleClientRpc(bool visible)
        {
            if (!IsClient) return;
            var list = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < list.Length; i++) if (list[i]) list[i].SetVisible(visible);
        }

        void EndSpawnSelect(bool success)
        {
            _selecting = false;
            ShowCanvas(spawnCanvas, false);

            if (_spawnCursor) Destroy(_spawnCursor);

            if (_inSpawnCam)
            {
                if (_isoCam)
                {
                    _isoCam.follow = _originalFollow;
                    _isoCam.enabled = true;
                }
                _inSpawnCam = false;
            }
        }

        void FreezeAllPlayers(bool frozen)
        {
            foreach (var cc in NetworkManager.ConnectedClientsList)
            {
                if (!cc.PlayerObject) continue;
                var pn = cc.PlayerObject.GetComponent<PlayerNetwork>();
                if (pn) pn.SetFrozenServer(frozen);
            }
        }

        TeamId GetMyTeam()
        {
            var nm = NetworkManager.Singleton;
            if (!nm || !nm.IsClient) return TeamId.A;
            var localPlayer = nm.LocalClient?.PlayerObject?.GetComponent<PlayerNetwork>();
            return localPlayer ? localPlayer.GetTeam() : TeamId.A;
        }

        [ClientRpc]
        void CountdownClientRpc(int number)
        {
            if (number > 0) PulseCountdown(number);
            else ClearCountdownUI();
        }

        void PauseInputFor(ulong clientId, bool paused) =>
            SetPlayerInputPausedClientRpc(paused, ToClient(clientId));

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
            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
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

        // helpers

        Bounds TeamBoundsA() => areas.GetTeamCollider(TeamId.A).bounds;
        Bounds TeamBoundsB() => areas.GetTeamCollider(TeamId.B).bounds;

        static ClientRpcParams ToClient(ulong clientId) =>
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };

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
