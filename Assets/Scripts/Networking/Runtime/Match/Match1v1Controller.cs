using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using TMPro;

namespace Game.Net
{
    public enum MatchState : byte
    {
        Waiting = 0,
        Countdown = 1,
        FlyIn = 2,
        SpawnSelect = 3,
        Playing = 4,
        RoundEnd = 5,
        MatchEnd = 6
    }

    [DefaultExecutionOrder(-9000)]
    public sealed class Match1v1Controller : NetworkBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Match1v1Areas areas;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("HUD")]
        [SerializeField] private CanvasGroup statusCanvas;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text countdownText;
        [SerializeField] private CanvasGroup spawnCanvas;
        [SerializeField] private TMP_Text spawnHintText;

        [Header("Score/Timer UI")]
        [SerializeField] private TMP_Text roundTimerText;
        [SerializeField] private TMP_Text scoreTextA;
        [SerializeField] private TMP_Text scoreTextB;
        [SerializeField] private TMP_Text roundNumberText;

        [Header("Win Panel")]
        [SerializeField] private CanvasGroup winPanel;
        [SerializeField] private TMP_Text winnerText;
        [SerializeField] private UnityEngine.UI.Button returnToLobbyButton;

        [Header("Cinematic")]
        [SerializeField] private GameObject shipPrefab;
        [SerializeField] private Transform shipStart;
        [SerializeField] private Transform shipEnd;
        [SerializeField, Min(0.1f)] private float shipDuration = 3f;
        [SerializeField] private string seatMountName = "SeatMount";
        [SerializeField] private string cameraMountName = "CameraMount";
        [SerializeField] private string cameraLookAtName = "CameraLookAt";

        [Header("Spawn Select")]
        [SerializeField] private GameObject spawnCursorPrefab;
        [SerializeField] private Vector3 spawnCameraPosition = new Vector3(0, 50, -20);
        [SerializeField] private Vector3 spawnCameraLookAt = new Vector3(0, 0, 0);

        [Header("Timings")]
        [SerializeField, Min(1f)] private int countdownSeconds = 3;
        [SerializeField, Min(3f)] private float cinematicSeconds = 3.5f;
        [SerializeField, Min(3f)] private float spawnSelectSeconds = 10f;
        [SerializeField, Min(10f)] private float roundDurationSeconds = 60f;
        [SerializeField, Min(2f)] private float roundEndDelaySeconds = 3f;

        [Header("Win Conditions")]
        [SerializeField, Min(1)] private int winsNeeded = 5;
        [SerializeField, Min(1)] private int winLeadNeeded = 2;
        [SerializeField, Min(5)] private int suddenDeathAt = 7;

        [Header("Spawn Prefab")]
        [Tooltip("Same prefab used by GamePlayerSpawner")]
        [SerializeField] private NetworkObject playerPrefab;

        [Header("Safety")]
        [SerializeField, Min(0.1f)] private float roundStartGraceSeconds = 1.5f;

        // Network Variables
        private readonly NetworkVariable<MatchState> _state = new();
        private readonly NetworkVariable<int> _playerCount = new();
        private readonly NetworkVariable<int> _roundNumber = new();
        private readonly NetworkVariable<float> _roundEndTime = new();
        private readonly NetworkVariable<int> _winsTeamA = new();
        private readonly NetworkVariable<int> _winsTeamB = new();
        private readonly NetworkVariable<bool> _suddenDeath = new();

        // Server state
        private readonly Dictionary<ulong, TeamId> _teams = new();
        private readonly Dictionary<ulong, Vector3> _chosenSpawns = new();
        private float _spawnDeadlineServer;
        private bool _firstRound = true;
        private float _roundStartTimeServer;

        // Client state
        private bool _selecting;
        private Bounds _myAreaBounds;
        private float _spawnDeadlineLocal;
        private Coroutine _flyCo, _selectCo, _uiCo;
        private GameObject _spawnCursor;
        private GameObject _shipInstance;

        // Camera
        private Camera _cam;
        private IsometricCamera _isoCam;
        private Transform _originalFollow;
        private Vector3 _preCinematicCamPos;
        private Quaternion _preCinematicCamRot;

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
            _roundNumber.OnValueChanged += (_, __) => RefreshUI();
            _winsTeamA.OnValueChanged += (_, __) => RefreshUI();
            _winsTeamB.OnValueChanged += (_, __) => RefreshUI();

            if (IsClient)
            {
                _cam = Camera.main;
                if (_cam) _isoCam = _cam.GetComponent<IsometricCamera>();
                RefreshUI();

                if (returnToLobbyButton)
                    returnToLobbyButton.onClick.AddListener(OnReturnToLobby);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            if (returnToLobbyButton)
                returnToLobbyButton.onClick.RemoveListener(OnReturnToLobby);
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

            if (_playerCount.Value < 2 && _state.Value != MatchState.MatchEnd)
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
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] != NetworkManager.ServerClientId) count++;
            _playerCount.Value = count;
        }

        void AssignTeamsIfNeeded()
        {
            var ids = NetworkManager.ConnectedClientsIds;
            int teamIndex = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var cid = ids[i];
                if (cid == NetworkManager.ServerClientId) continue;
                if (!_teams.ContainsKey(cid))
                {
                    _teams[cid] = (TeamId)(teamIndex % 2);
                    teamIndex++;
                }

                var player = NetworkManager.ConnectedClients[cid]?.PlayerObject?.GetComponent<PlayerNetwork>();
                if (player) player.SetTeam(_teams[cid]);
            }
        }

        void TryStartFlow()
        {
            if (_state.Value != MatchState.Waiting) return;
            if (_playerCount.Value < 2) return;
            StartCoroutine(CoStartMatch());
        }

        IEnumerator CoStartMatch()
        {
            _roundNumber.Value = 1;
            _winsTeamA.Value = 0;
            _winsTeamB.Value = 0;
            _suddenDeath.Value = false;
            _firstRound = true;

            yield return StartRound();
        }

        IEnumerator StartRound()
        {
            _state.Value = MatchState.Countdown;
            for (int i = countdownSeconds; i > 0; i--)
            {
                CountdownClientRpc(i);
                yield return new WaitForSecondsRealtime(1f);
            }
            CountdownClientRpc(0);
            yield return new WaitForSecondsRealtime(0.25f);

            if (_firstRound)
            {
                _firstRound = false;
                _state.Value = MatchState.FlyIn;
                BroadcastPauseAll(true);
                FreezeAllPlayers(true);
                SetAllPlayersVisibleClientRpc(true);
                StartCinematicClientRpc();
                yield return new WaitForSecondsRealtime(cinematicSeconds);
                // Despawn everyone after the fly-in.
                DespawnAllPlayersServer();
                SetAllPlayersVisibleClientRpc(false);
            }

            StartSpawnSelect();
        }

        void StartSpawnSelect()
        {
            _state.Value = MatchState.SpawnSelect;
            _spawnDeadlineServer = Time.unscaledTime + spawnSelectSeconds;
            _chosenSpawns.Clear();

            foreach (var kv in _teams)
            {
                var cid = kv.Key;
                var b = areas.GetTeamCollider(kv.Value).bounds;
                BeginSpawnSelectClientRpc(b.center, b.size, spawnSelectSeconds, ToClient(cid));
            }

            StartCoroutine(CoWatchSpawnDeadline());
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
            if (_cam)
            {
                _preCinematicCamPos = _cam.transform.position;
                _preCinematicCamRot = _cam.transform.rotation;
            }

            if (_isoCam) _isoCam.enabled = false;

            _shipInstance = shipPrefab ? Instantiate(shipPrefab) : null;
            if (!_shipInstance || !shipStart || !shipEnd)
            {
                Debug.LogError("[Match1v1] Ship or waypoints missing");
                yield break;
            }

            Transform seatMount = FindDeep(_shipInstance.transform, seatMountName) ?? _shipInstance.transform;
            Transform cameraMount = FindDeep(_shipInstance.transform, cameraMountName);
            Transform lookAt = FindDeep(_shipInstance.transform, cameraLookAtName);

            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var player in players)
            {
                if (!player) continue;
                player.transform.SetParent(seatMount, false);
                float xOffset = player.GetTeam() == TeamId.A ? -1.5f : 1.5f;
                player.transform.localPosition = new Vector3(xOffset, 0, 0);
                player.transform.localRotation = Quaternion.identity;
                player.SetVisible(true);
            }

            if (_cam && cameraMount && lookAt)
            {
                _cam.transform.SetParent(cameraMount, false);
                _cam.transform.localPosition = Vector3.zero;
                _cam.transform.localRotation = Quaternion.identity;
                _cam.transform.LookAt(lookAt.position);
            }

            float elapsed = 0;
            while (elapsed < shipDuration)
            {
                float t = elapsed / shipDuration;
                _shipInstance.transform.position = Vector3.Lerp(shipStart.position, shipEnd.position, t);
                _shipInstance.transform.rotation = Quaternion.Slerp(shipStart.rotation, shipEnd.rotation, t);
                elapsed += Time.deltaTime;
                yield return null;
            }

            foreach (var player in players)
                if (player) player.transform.SetParent(null, true);

            if (_cam) _cam.transform.SetParent(null, true);
            if (_isoCam) _isoCam.enabled = true;

            Destroy(_shipInstance);
            _shipInstance = null;
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

            if (!_spawnCursor)
            {
                if (spawnCursorPrefab) _spawnCursor = Instantiate(spawnCursorPrefab);
                else
                {
                    _spawnCursor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    _spawnCursor.transform.localScale = new Vector3(1f, 0.2f, 1f);
                    Destroy(_spawnCursor.GetComponent<Collider>());
                    var rend = _spawnCursor.GetComponent<Renderer>();
                    if (rend)
                    {
                        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                        rend.material = new Material(shader);
                        rend.material.color = new Color(1f, 1f, 0f, 0.7f);
                    }
                }
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
                _cam.transform.rotation = Quaternion.LookRotation((spawnCameraLookAt - spawnCameraPosition).normalized, Vector3.up);
            }

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
            if (_selecting) EndSpawnSelect();
        }

        void Update()
        {
            if (_selecting && _cam)
            {
                var ray = _cam.ScreenPointToRay(Input.mousePosition);
                Vector3 point = Vector3.zero;
                bool valid = false;

                if (Physics.Raycast(ray, out RaycastHit hit, 2000f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    point = hit.point;
                    valid = ContainsXZ(_myAreaBounds, point);
                }
                if (!valid)
                {
                    var plane = new Plane(Vector3.up, new Vector3(0, _myAreaBounds.center.y, 0));
                    if (plane.Raycast(ray, out float dist))
                    {
                        point = ray.GetPoint(dist);
                        valid = ContainsXZ(_myAreaBounds, point);
                    }
                }

                if (valid)
                {
                    if (_spawnCursor)
                    {
                        _spawnCursor.transform.position = new Vector3(point.x, point.y + 0.5f, point.z);
                        _spawnCursor.SetActive(true);
                    }

                    if (Input.GetMouseButtonDown(0))
                    {
                        ChooseSpawnServerRpc(point);
                        EndSpawnSelect();
                    }
                }
                else if (_spawnCursor)
                {
                    _spawnCursor.SetActive(false);
                }
            }

            if (IsClient && _state.Value == MatchState.Playing && roundTimerText)
            {
                float timeLeft = Mathf.Max(0, _roundEndTime.Value - Time.time);
                int minutes = (int)(timeLeft / 60);
                int seconds = (int)(timeLeft % 60);
                roundTimerText.text = $"{minutes:0}:{seconds:00}";
            }
        }

        [ServerRpc(RequireOwnership = false)]
        void ChooseSpawnServerRpc(Vector3 point, ServerRpcParams rpc = default)
        {
            if (!IsServer || _state.Value != MatchState.SpawnSelect) return;

            ulong cid = rpc.Receive.SenderClientId;
            if (!_teams.TryGetValue(cid, out var team)) return;

            var b = areas.GetTeamCollider(team).bounds;
            if (!ContainsXZ(b, point)) return;

            // Store the exact XZ and let server snap Y to Ground on spawn.
            _chosenSpawns[cid] = new Vector3(point.x, b.center.y, point.z);

            if (_chosenSpawns.Count >= _playerCount.Value)
                SpawnAllAndStartRound();
        }

        IEnumerator CoWatchSpawnDeadline()
        {
            yield return new WaitForSecondsRealtime(spawnSelectSeconds + 0.1f);
            if (_state.Value == MatchState.SpawnSelect)
                SpawnAllAndStartRound();
        }

        void SpawnAllAndStartRound()
        {
            if (!playerPrefab)
            {
                Debug.LogError("[Match1v1] Player prefab not assigned on controller.");
                return;
            }

            // Spawn fresh PlayerObjects at chosen positions.
            var ids = NetworkManager.ConnectedClientsIds;
            foreach (var cid in ids)
            {
                if (cid == NetworkManager.ServerClientId) continue;
                if (!_teams.TryGetValue(cid, out var team)) continue;

                Vector3 point = _chosenSpawns.TryGetValue(cid, out var chosen)
                    ? chosen
                    : areas.GetRandomPoint(team);

                SpawnFreshPlayerForClient(cid, point, team);
            }

            _state.Value = MatchState.Playing;
            _roundStartTimeServer = Time.time;
            _roundEndTime.Value = Time.time + roundDurationSeconds;

            SetAllPlayersVisibleClientRpc(true);
            FreezeAllPlayers(false);
            BroadcastPauseAll(false);
            EndSpawnSelectForAllClientRpc();

            StartCoroutine(CoMonitorRound());
        }

        void SpawnFreshPlayerForClient(ulong clientId, Vector3 point, TeamId team)
        {
            // Create at point, then clamp to Ground, then SpawnAsPlayerObject.
            var inst = Instantiate(playerPrefab);
            var t = inst.transform;

            // Face neutral center.
            Vector3 look = areas.GetNeutralCenter() - point;
            look.y = 0f;
            var rot = look.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : Quaternion.identity;

            t.SetPositionAndRotation(point, rot);

            // Snap to Ground layer before spawning.
            var capsule = inst.GetComponent<CapsuleCollider>();
            GroundClampServer.SnapToGround(t, EffectiveGroundMask(), 0.02f, capsule, 10f, 50f);

            // Ensure prefab is registered then spawn.
            try { NetworkManager.AddNetworkPrefab(inst.gameObject); } catch { }
            inst.SpawnAsPlayerObject(clientId);

            // Initialize team and health post-spawn.
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn)
            {
                pn.SetTeam(team);
                pn.SetHealth(100f);
            }
        }

        IEnumerator CoMonitorRound()
        {
            while (_state.Value == MatchState.Playing)
            {
                bool someoneAlive_A = false, someoneAlive_B = false;
                float healthSum_A = 0, healthSum_B = 0;
                int count_A = 0, count_B = 0;

                foreach (var cid in NetworkManager.ConnectedClientsIds)
                {
                    if (cid == NetworkManager.ServerClientId) continue;
                    var player = NetworkManager.ConnectedClients[cid]?.PlayerObject?.GetComponent<PlayerNetwork>();
                    if (player == null) continue;

                    float health = player.GetHealth();
                    TeamId team = player.GetTeam();

                    if (team == TeamId.A)
                    {
                        healthSum_A += health;
                        count_A++;
                        if (health > 0) someoneAlive_A = true;
                    }
                    else
                    {
                        healthSum_B += health;
                        count_B++;
                        if (health > 0) someoneAlive_B = true;
                    }
                }

                // Grace: wait until both teams actually have a spawned player, and initial settle time.
                if (Time.time < _roundStartTimeServer + roundStartGraceSeconds || count_A == 0 || count_B == 0)
                {
                    yield return new WaitForSeconds(0.25f);
                    continue;
                }

                int roundWinner = -1;

                if (!someoneAlive_A && someoneAlive_B) roundWinner = 1;      // B wins
                else if (!someoneAlive_B && someoneAlive_A) roundWinner = 0; // A wins
                else if (Time.time >= _roundEndTime.Value)
                {
                    if      (healthSum_A > healthSum_B) roundWinner = 0;
                    else if (healthSum_B > healthSum_A) roundWinner = 1;
                    else roundWinner = -1; // draw
                }

                if (roundWinner != -1 || Time.time >= _roundEndTime.Value)
                {
                    EndRound(roundWinner);
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        void EndRound(int winnerTeam)
        {
            _state.Value = MatchState.RoundEnd;

            if (winnerTeam == 0) _winsTeamA.Value++;
            else if (winnerTeam == 1) _winsTeamB.Value++;

            ShowRoundEndClientRpc(winnerTeam);
            StartCoroutine(CoPostRoundFlow());
        }

        IEnumerator CoPostRoundFlow()
        {
            yield return new WaitForSecondsRealtime(roundEndDelaySeconds);

            bool matchOver = false;
            TeamId? matchWinner = null;

            int winsA = _winsTeamA.Value;
            int winsB = _winsTeamB.Value;

            if (winsA >= winsNeeded && winsA - winsB >= winLeadNeeded)
            {
                matchOver = true; matchWinner = TeamId.A;
            }
            else if (winsB >= winsNeeded && winsB - winsA >= winLeadNeeded)
            {
                matchOver = true; matchWinner = TeamId.B;
            }
            else if (winsA >= suddenDeathAt && winsB >= suddenDeathAt)
            {
                _suddenDeath.Value = true;
                if (winsA > winsB) { matchOver = true; matchWinner = TeamId.A; }
                else if (winsB > winsA) { matchOver = true; matchWinner = TeamId.B; }
            }

            if (matchOver && matchWinner.HasValue)
            {
                EndMatch(matchWinner.Value);
            }
            else
            {
                // Prepare next round: despawn everyone and run the flow again.
                SetAllPlayersVisibleClientRpc(false);
                FreezeAllPlayers(true);
                DespawnAllPlayersServer();
                _roundNumber.Value++;
                yield return StartRound();
            }
        }

        void EndMatch(TeamId winner)
        {
            _state.Value = MatchState.MatchEnd;
            ShowMatchEndClientRpc(winner);
        }

        [ClientRpc]
        void ShowRoundEndClientRpc(int winnerTeam)
        {
            if (statusText)
            {
                if (winnerTeam >= 0)
                    statusText.text = $"Round {_roundNumber.Value} - Team {(TeamId)winnerTeam} Wins!";
                else
                    statusText.text = $"Round {_roundNumber.Value} - Draw!";
            }
            ShowCanvas(statusCanvas, true);
        }

        [ClientRpc]
        void ShowMatchEndClientRpc(TeamId winner)
        {
            if (winPanel)
            {
                ShowCanvas(winPanel, true);
                if (winnerText)
                    winnerText.text = $"Team {winner} Wins the Match!\n{_winsTeamA.Value} - {_winsTeamB.Value}";
            }
        }

        [ClientRpc]
        void EndSpawnSelectForAllClientRpc()
        {
            if (_selecting) EndSpawnSelect();
        }

        void EndSpawnSelect()
        {
            _selecting = false;
            ShowCanvas(spawnCanvas, false);
            SpawnAreaHighlighter.SetMode(SpawnAreaHighlighter.Mode.Hidden, new Bounds());

            if (_spawnCursor)
            {
                Destroy(_spawnCursor);
                _spawnCursor = null;
            }

            if (_isoCam)
            {
                _isoCam.follow = _originalFollow;
                _isoCam.enabled = true;
            }
        }

        void OnReturnToLobby()
        {
            NetworkManager.Singleton.Shutdown();
            UnityEngine.SceneManagement.SceneManager.LoadScene("Lobby");
        }

        static bool ContainsXZ(Bounds b, Vector3 p)
        {
            return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (!root || string.IsNullOrEmpty(name)) return null;
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all) if (t && t.name == name) return t;
            return null;
        }

        void FreezeAllPlayers(bool frozen)
        {
            foreach (var cc in NetworkManager.ConnectedClientsList)
            {
                var pn = cc.PlayerObject?.GetComponent<PlayerNetwork>();
                if (pn) pn.SetFrozenServer(frozen);
            }
        }

        void PauseInputFor(ulong clientId, bool paused) =>
            SetPlayerInputPausedClientRpc(paused, ToClient(clientId));

        void BroadcastPauseAll(bool paused)
        {
            foreach (var cid in NetworkManager.ConnectedClientsIds)
                if (cid != NetworkManager.ServerClientId)
                    PauseInputFor(cid, paused);
        }

        [ClientRpc]
        void SetPlayerInputPausedClientRpc(bool paused, ClientRpcParams p = default)
        {
            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var pl in players)
                if (pl && pl.IsOwner) pl.SetInputPaused(paused);

            if (paused && statusText && statusCanvas)
            {
                statusText.text = _state.Value == MatchState.Waiting
                    ? $"Waiting for {_playerCount.Value}/2 players"
                    : "Please wait";
                ShowCanvas(statusCanvas, true);
            }
        }

        [ClientRpc]
        void SetAllPlayersVisibleClientRpc(bool visible)
        {
            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var pl in players) if (pl) pl.SetVisible(visible);
        }

        [ClientRpc]
        void CountdownClientRpc(int number)
        {
            if (countdownText)
            {
                countdownText.text = number > 0 ? number.ToString() : "";
                if (number > 0 && _uiCo != null) StopCoroutine(_uiCo);
                if (number > 0) _uiCo = StartCoroutine(CoPulse(countdownText));
            }
        }

        IEnumerator CoPulse(TMP_Text t)
        {
            if (!t) yield break;
            var rt = t.rectTransform;
            float duration = 0.6f;
            float elapsed = 0;
            while (elapsed < duration)
            {
                float scale = 1.0f + 0.35f * Mathf.Sin((elapsed / duration) * Mathf.PI);
                rt.localScale = Vector3.one * scale;
                elapsed += Time.deltaTime;
                yield return null;
            }
            rt.localScale = Vector3.one;
        }

        void RefreshUI()
        {
            if (!IsClient) return;

            if (scoreTextA) scoreTextA.text = $"Team A: {_winsTeamA.Value}";
            if (scoreTextB) scoreTextB.text = $"Team B: {_winsTeamB.Value}";
            if (roundNumberText)
            {
                string roundText = $"Round {_roundNumber.Value}";
                if (_suddenDeath.Value) roundText += " (SUDDEN DEATH)";
                roundNumberText.text = roundText;
            }

            if (_state.Value == MatchState.Waiting)
            {
                ShowCanvas(statusCanvas, true);
                if (statusText)
                    statusText.text = _playerCount.Value >= 2
                        ? "2 players connected. Match starting..."
                        : $"Waiting for {_playerCount.Value}/2 players";
            }
            else if (_state.Value == MatchState.Playing)
            {
                ShowCanvas(statusCanvas, false);
            }
        }

        static void ShowCanvas(CanvasGroup cg, bool show)
        {
            if (!cg) return;
            cg.alpha = show ? 1f : 0f;
            cg.interactable = show;
            cg.blocksRaycasts = show;
            if (cg.gameObject.activeSelf != show)
                cg.gameObject.SetActive(show);
        }

        static ClientRpcParams ToClient(ulong clientId) =>
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };

        // Despawn all current PlayerObjects. Called after fly-in and between rounds.
        void DespawnAllPlayersServer()
        {
            if (!IsServer) return;

            foreach (var cc in NetworkManager.ConnectedClientsList)
            {
                if (cc.ClientId == NetworkManager.ServerClientId) continue;
                var po = cc.PlayerObject;
                if (po && po.IsSpawned)
                {
                    po.Despawn(true); // destroy object on all peers
                }
            }
        }

        LayerMask EffectiveGroundMask()
        {
            if (groundMask.value != 0) return groundMask;
            int g = LayerMask.NameToLayer("Ground");
            return g >= 0 ? (LayerMask)(1 << g) : ~0;
        }

        // Public method for external health updates
        public void UpdatePlayerHealth(ulong clientId, float healthDelta)
        {
            if (!IsServer) return;
            var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var cc) ? cc.PlayerObject?.GetComponent<PlayerNetwork>() : null;
            if (player == null) return;
            float newHealth = Mathf.Clamp(player.GetHealth() + healthDelta, 0f, 100f);
            player.SetHealth(newHealth);
        }
    }
}
