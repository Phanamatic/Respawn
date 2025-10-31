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
        [SerializeField, Tooltip("Optional lightweight visual used if no PlayerNetwork exists yet.")]
        private GameObject cinematicStandInPrefab;   // visual-only prefab (no NetworkObject)

        [Header("Spawn Select")]
        [SerializeField] private GameObject spawnCursorPrefab;
        [SerializeField] private Vector3 spawnCameraPosition = new Vector3(0, 50, -20);
        [SerializeField] private Vector3 spawnCameraLookAt = new Vector3(0, 0, 0);

        [Header("Timings")]
        [SerializeField, Min(1f)] private int countdownSeconds = 3;
        [SerializeField, Min(3f)] private float cinematicSeconds = 3.5f;
        [SerializeField, Min(3f)] private float spawnSelectSeconds = 15f;
        [SerializeField, Min(10f)] private float roundDurationSeconds = 90f;
        [SerializeField, Min(0f)] private float roundEndDelaySeconds = 0f;

        [Header("Win Conditions")]
        [SerializeField, Min(1)] private int winsNeeded = 5;
        [SerializeField, Min(1)] private int winLeadNeeded = 2;
        [SerializeField, Min(5)] private int suddenDeathAt = 7;

        [Header("Spawn Prefab")]
        [Tooltip("Same prefab used by GamePlayerSpawner")]
        [SerializeField] private NetworkObject playerPrefab;

        [Header("Safety")]
        [SerializeField, Min(0.1f)] private float roundStartGraceSeconds = 1.5f;

        [Header("Required Players")]
        [SerializeField] private TMP_Text _requiredPlayersUI;
        [SerializeField, Min(2)] private int requiredPlayers = 2;

        [Header("Intro Pan")]
        [SerializeField] Transform mapPanStart;
        [SerializeField] Transform mapPanEnd;
        [SerializeField, Min(0.1f)] float mapPanSeconds = 3f;

        Coroutine _cineCo, _panCo;   // track coroutines so we can stop safely

        [Header("Spawn Select Camera Target")]
        [SerializeField] Transform spawnSelectLookTarget;

        [Header("Fade During Selection")]
        [Tooltip("These renderers fade to 30% opacity while players choose spawns.")]
        [SerializeField] Renderer[] fadeDuringSelect;
        [Range(0f,1f)] [SerializeField] float selectFadeAlpha = 0.3f;
        Dictionary<Renderer, Color[]> _origFadeColors = new Dictionary<Renderer, Color[]>();

        [Header("Spawn Camera Framing (Top-Down)")]
        [SerializeField, Min(1f)] float spawnCamMargin = 1.08f;  // % margin around map
        [SerializeField, Min(10f)] float spawnCamHeight = 80f;   // just needs to clear scenery when orthographic=false
        [SerializeField] bool spawnCamUseOrthographic = true;

        bool _didIntroPanThisRound;
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
        private bool _myAreaBlocked;
        private TeamId _myTeam;
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
            UpdateRequiredPlayersClientRpc(_playerCount.Value, requiredPlayers);
            TryStartFlow();
        }

        void OnClientDisconnected(ulong clientId)
        {
            _teams.Remove(clientId);
            _chosenSpawns.Remove(clientId);
            RecountPlayers();
            UpdateRequiredPlayersClientRpc(_playerCount.Value, requiredPlayers);

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
            RefreshUIClientRpc();
        }

        void AssignTeamsIfNeeded()
        {
            var all = NetworkManager.ConnectedClientsIds;
            var nonServer = new List<ulong>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var cid = all[i];
                if (cid != NetworkManager.ServerClientId) nonServer.Add(cid);
            }

            // Stable order
            nonServer.Sort();

            for (int i = 0; i < nonServer.Count; i++)
            {
                var cid = nonServer[i];
                var want = (TeamId)(i % 2);
                if (!_teams.TryGetValue(cid, out var have) || have != want)
                    _teams[cid] = want;

                var player = NetworkManager.ConnectedClients[cid]?.PlayerObject?.GetComponent<PlayerNetwork>();
                if (player) player.SetTeam(want);
            }
        }

        void TryStartFlow()
        {
            if (_state.Value != MatchState.Waiting) return;
            if (_playerCount.Value < requiredPlayers) return;
            StartCoroutine(CoStartMatch());
        }

        IEnumerator CoStartMatch()
        {
            _roundNumber.Value = 1;
            _winsTeamA.Value = 0;
            _winsTeamB.Value = 0;
            _suddenDeath.Value = false;
            _firstRound = true;
            _didIntroPanThisRound = false;

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

            // Halftime side swap: from round > ceil(winsNeeded/2) we flip Team A/B spawn sides.
            if (areas)
            {
                bool swap = _roundNumber.Value > Mathf.CeilToInt(winsNeeded / 2f);
                areas.SetSwapSides(swap);
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
                var team = kv.Value;
                if (!areas)
                {
                    Debug.LogError("[Match1v1] Areas reference missing; cannot determine spawn zone.");
                    continue;
                }

                var bounds = areas.GetTeamBounds(team);
                bool blocked = areas.IsAreaBlocked(team);
                BeginSpawnSelectClientRpc(bounds.center, bounds.size, spawnSelectSeconds, blocked, team, ToClient(cid));
            }

            StartCoroutine(CoWatchSpawnDeadline());
        }

        [ClientRpc]
        void StartCinematicClientRpc()
        {
            if (!IsClient) return;
            if (_cineCo != null) { StopCoroutine(_cineCo); _cineCo = null; }
            _cineCo = StartCoroutine(CoFlyIn());
        }

        IEnumerator CoFlyIn()
        {
            if (!AcquireCameraSafe()) yield break;

            _preCinematicCamPos = _cam.transform.position;
            _preCinematicCamRot = _cam.transform.rotation;

#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (_isoCam == null) _isoCam = UnityEngine.Object.FindFirstObjectByType<IsometricCamera>(FindObjectsInactive.Include);
#else
            if (_isoCam == null) _isoCam = UnityEngine.Object.FindObjectOfType<IsometricCamera>();
#endif
            if (_isoCam) _isoCam.enabled = false;

            _shipInstance = shipPrefab ? Instantiate(shipPrefab) : null;
            if (!_shipInstance || !shipStart || !shipEnd)
            {
                Debug.LogError("[Match1v1] Ship or waypoints missing");
                yield break;
            }

            // Mounts
            Transform seatMount   = EnsureSeatMount(_shipInstance.transform, seatMountName);
            Transform cameraMount = FindDeep(_shipInstance.transform, cameraMountName);
            Transform lookAt      = FindDeep(_shipInstance.transform, cameraLookAtName);

            // Always use client-only stand-ins on the ship. Never reparent networked player objects.
            var players       = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var hiddenAnchors = new List<PlayerVisualAnchor>(players.Length);
            var tempStandIns  = new List<GameObject>(2);

            // Hide real models during cinematic
            foreach (var p in players)
            {
                if (!p) continue;
                var a = p.GetComponent<PlayerVisualAnchor>() ?? p.gameObject.AddComponent<PlayerVisualAnchor>();
                a.SetModelVisible(false);
                hiddenAnchors.Add(a);

                // pick seat by team for nice framing
                float xOffset = p.GetTeam() == TeamId.A ? -1.5f : 1.5f;
                var prefab = cinematicStandInPrefab ? cinematicStandInPrefab : (playerPrefab ? playerPrefab.gameObject : null);
                var stand = prefab ? Instantiate(prefab) : null;
                if (stand)
                {
                    foreach (var no in stand.GetComponentsInChildren<NetworkObject>(true)) Destroy(no);
                    foreach (var rb in stand.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
                    foreach (var col in stand.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    foreach (var beh in stand.GetComponentsInChildren<Behaviour>(true))
                        if (!(beh is Animator)) beh.enabled = false;

                    stand.transform.SetParent(seatMount, false);
                    stand.transform.localPosition = new Vector3(xOffset, 0, 0);
                    stand.transform.localRotation = Quaternion.identity;
                    tempStandIns.Add(stand);
                }
            }

            // If no players yet, still show two generic riders
            if (players.Length == 0)
            {
                for (int i = 0; i < 2; i++)
                {
                    var prefab = cinematicStandInPrefab ? cinematicStandInPrefab : (playerPrefab ? playerPrefab.gameObject : null);
                    var stand = prefab ? Instantiate(prefab) : null;
                    if (!stand) break;

                    foreach (var no in stand.GetComponentsInChildren<NetworkObject>(true)) Destroy(no);
                    foreach (var rb in stand.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
                    foreach (var col in stand.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    foreach (var beh in stand.GetComponentsInChildren<Behaviour>(true))
                        if (!(beh is Animator)) beh.enabled = false;

                    float xOffset = (i == 0) ? -1.5f : 1.5f;
                    stand.transform.SetParent(seatMount, false);
                    stand.transform.localPosition = new Vector3(xOffset, 0, 0);
                    stand.transform.localRotation = Quaternion.identity;
                    tempStandIns.Add(stand);
                }
            }

            // Camera attach
            if (AcquireCameraSafe() && cameraMount && lookAt)
            {
                _cam.transform.SetParent(cameraMount, false);
                _cam.transform.localPosition = Vector3.zero;
                _cam.transform.localRotation = Quaternion.identity;
                _cam.transform.LookAt(lookAt.position);
            }

            // Animate ship
            float elapsed = 0f;
            while (_shipInstance && elapsed < shipDuration)
            {
                float t = elapsed / shipDuration;
                _shipInstance.transform.position = Vector3.Lerp(shipStart.position, shipEnd.position, t);
                _shipInstance.transform.rotation = Quaternion.Slerp(shipStart.rotation, shipEnd.rotation, t);
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Restore models and cleanup
            for (int i = 0; i < hiddenAnchors.Count; i++)
                if (hiddenAnchors[i]) hiddenAnchors[i].SetModelVisible(true);

            for (int i = 0; i < tempStandIns.Count; i++)
                if (tempStandIns[i]) Destroy(tempStandIns[i]);

            // Detach camera but DO NOT destroy the ship yet.
            // Ship stays visible until spawn-select actually begins.
            DetachCameraFromShip();
            if (_isoCam) _isoCam.enabled = true;
        }

        [ClientRpc]
        void BeginSpawnSelectClientRpc(Vector3 areaCenter, Vector3 areaSize, float seconds, bool blocked, TeamId team, ClientRpcParams p = default)
        {
            if (!IsClient) return;

            _myAreaBounds = new Bounds(areaCenter, areaSize);
            _spawnDeadlineLocal = Time.unscaledTime + seconds;
            _myAreaBlocked = blocked;
            _myTeam = team;
            _selecting = true;

            // Highlight with carved holes from overlapping "No Spawn" triggers.
            List<Bounds> holes = null;
            if (areas) holes = areas.GetBlockerIntersectionsFor(_myAreaBounds, null);
            ApplySpawnHighlightLayout(holes);
// [Match1v1] Client sees green area with red "holes" subtracted.

            // Run the intro pan once, then open the spawn UI.
            if (!_didIntroPanThisRound && mapPanStart && mapPanEnd && _cam)
            {
                _didIntroPanThisRound = true;
                // Keep the ship until spawn-select begins. Only hide players/stand-ins and detach the camera.
                PrepareCinematicForPanLocal();
                StartCoroutine(CoIntroPanThenOpenSpawnUI());
                return;
            }

            // No pan path: frame camera from bounds and open UI now.
            FrameSpawnCameraFullMap();

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

                // Ensure cursor never blocks ground raycasts
                foreach (var col in _spawnCursor.GetComponentsInChildren<Collider>(true))
                    if (col) col.enabled = false;
                int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
                if (ignoreRaycast >= 0) _spawnCursor.layer = ignoreRaycast;
            }

            if (_spawnCursor)
                _spawnCursor.SetActive(false);

            ShowCanvas(spawnCanvas, true);
            if (spawnHintText)
                spawnHintText.text = Mathf.CeilToInt(seconds).ToString("0");

            // Fade configured objects during selection
            ApplySelectTransparency(true);

            if (_isoCam)
            {
                _originalFollow = _isoCam.follow;
                _isoCam.enabled = false;
            }
            if (_cam)
            {
                _cam.transform.position = spawnCameraPosition;
                if (spawnSelectLookTarget)
                {
                    var look = (spawnSelectLookTarget.position - _cam.transform.position).normalized;
                    _cam.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
                }
                else
                {
                    _cam.transform.rotation = Quaternion.LookRotation((spawnCameraLookAt - spawnCameraPosition).normalized, Vector3.up);
                }
            }

            if (_selectCo != null) StopCoroutine(_selectCo);
            _selectCo = StartCoroutine(CoSpawnSelectTimer());
        }

        void ApplySpawnHighlightLayout(List<Bounds> holes)
        {
            Bounds enemy = default;
            Bounds neutral = default;
            Bounds map = default;

            if (areas)
            {
                enemy = areas.GetTeamBounds(OpposingTeam(_myTeam));
                neutral = areas.GetNeutralBounds();
                map = areas.GetMapBounds();
            }

            if (map.size.sqrMagnitude <= 0f)
                map = _myAreaBounds;

            SpawnAreaHighlighter.SetLayout(
                SpawnAreaHighlighter.Mode.Choosing,
                _myAreaBounds,
                enemy,
                neutral,
                map,
                _myAreaBlocked,
                holes);
        }

        IEnumerator CoSpawnSelectTimer()
        {
            while (_selecting && Time.unscaledTime < _spawnDeadlineLocal)
            {
                if (spawnHintText)
                {
                    float remain = Mathf.Max(0f, _spawnDeadlineLocal - Time.unscaledTime);
                    spawnHintText.text = Mathf.CeilToInt(remain).ToString("0");
                }
                yield return null;
            }
            if (_selecting) EndSpawnSelect();
        }

        void Update()
        {
            // Avoid nulls before NGO starts
            if (!Application.isPlaying) return;

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

                // Cursor should show on green only: hide over carved holes.
                if (valid && areas && areas.IsPointBlockedInBounds(_myAreaBounds, point))
                    valid = false;

                if (valid)
                {
                    if (_spawnCursor)
                    {
                        // Place the cursor exactly on ground (slight lift to avoid z-fight)
                        float gy = GroundYAt(point);
                        _spawnCursor.transform.position = new Vector3(point.x, gy + 0.02f, point.z);
                        _spawnCursor.SetActive(true);
                    }
// Cursor now appears exactly on the ground at the mouse position during spawn select.

                    if (Input.GetMouseButtonDown(0))
                    {
                        if (areas && !areas.IsPointBlockedInBounds(_myAreaBounds, point))
                        {
                            ChooseSpawnServerRpc(point);
                            EndSpawnSelect();
                        }
                        else
                        {
                            // brief invalid feedback already handled by UI coroutine in this class
                            if (spawnHintText)
                            {
                                StopCoroutineSafe(ref _uiCo);
                                _uiCo = StartCoroutine(CoFlashInvalid(spawnHintText));
                            }
                        }
                    }
// [Match1v1] Client click respects red holes.
                }
                else if (_spawnCursor)
                {
                    _spawnCursor.SetActive(false);
                }
            }

            if (IsClient && _state.Value == MatchState.Playing && roundTimerText)
            {
                var serverNow = GetServerNowSafe();
                var t = Mathf.Max(0f, _roundEndTime.Value - serverNow);
                int minutes = (int)(t / 60);
                int seconds = (int)(t % 60);
                roundTimerText.text = $"{minutes:0}:{seconds:00}";
            }
        }

        [ServerRpc(RequireOwnership = false)]
        void ChooseSpawnServerRpc(Vector3 point, ServerRpcParams rpc = default)
        {
            if (!IsServer || _state.Value != MatchState.SpawnSelect) return;

            ulong cid = rpc.Receive.SenderClientId;
            if (!_teams.TryGetValue(cid, out var team)) return;

            if (!areas) return;

            var b = areas.GetTeamBounds(team);
            if (!ContainsXZ(b, point)) return;

            // Fine-grained validation only: allow click if it's inside the area and not inside a blocker.
            if (areas.IsPointBlockedForTeam(team, point)) return;

            // Store the exact XZ and let server snap Y to Ground on spawn.
            _chosenSpawns[cid] = new Vector3(point.x, b.center.y, point.z);
// [Match1v1] Server authority validates chosen point against blockers.

            if (_chosenSpawns.Count >= 2)
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

                if (!areas)
                {
                    Debug.LogError("[Match1v1] Areas reference missing during spawn; defaulting to controller position.");
                    SpawnFreshPlayerForClient(cid, transform.position, team);
                    continue;
                }

                Vector3 point;
                if (_chosenSpawns.TryGetValue(cid, out var chosen))
                {
                    point = chosen;
                }
                else
                {
                    // Only sample from **unblocked** sub-areas (respects carved holes)
                    point = areas.GetRandomUnblockedPoint(team, 128);
                }

                SpawnFreshPlayerForClient(cid, point, team);
            }

            _state.Value = MatchState.Playing;
            // server-authoritative start/end; guard if server time not ready yet
            var now = GetServerNowSafe();
            _roundStartTimeServer = now;
            _roundEndTime.Value   = now + roundDurationSeconds;

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
            Vector3 look = areas.GetNeutralCenter() - point; // neutral center now from unified split if enabled
            look.y = 0f;
            var rot = look.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : Quaternion.identity;

            t.SetPositionAndRotation(point, rot);

            // Snap to Ground and, if obstructed by geometry, slide to nearest clear ground beside it.
            var capsule = inst.GetComponent<CapsuleCollider>();
            var gmask = EffectiveGroundMask();
            GroundClampServer.SnapToGround(t, gmask, 0.02f, capsule, 10f, 50f);

            // If still intersecting, try nearest clear on ground
            if (!GroundClampServer.TryFindNearestClearGround(t.position, out var clear, gmask, capsule, 0.02f, 10f, 50f, 4f, 6, 24))
            {
                // keep snapped position even if blocked; depenetration will resolve
            }
            else
            {
                t.position = clear;
            }

            // Ensure GroundClampServer exists on server to maintain clamp
            if (!inst.GetComponent<GroundClampServer>())
                inst.gameObject.AddComponent<GroundClampServer>();

            // Ensure prefab is registered then spawn.
            try { NetworkManager.AddNetworkPrefab(inst.gameObject); } catch { }
            inst.SpawnAsPlayerObject(clientId);

            // Initialize team and health post-spawn.
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn)
            {
                pn.SetTeam(team);
                pn.SetHealth(100f);
                pn.ServerAutoEquipPrimary(); // force Primary on start
            }
// Players spawn already holding Primary.
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
                else if (GetServerNowSafe() >= _roundEndTime.Value)
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
            else { _winsTeamA.Value++; _winsTeamB.Value++; } // draw => both get a point

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
            // Force camera/UI cleanup even if this client never entered selection (random assignment / late join).
            EndSpawnSelect(force: true);
        }

        void EndSpawnSelect(bool force = false)
        {
            if (!force && !_selecting) return;

            _selecting = false;
            _myAreaBlocked = false;
            ShowCanvas(spawnCanvas, false);
            SpawnAreaHighlighter.SetLayout(SpawnAreaHighlighter.Mode.Hidden, default, default, default, default, false, null);

            // Restore transparency
            ApplySelectTransparency(false);

            if (_spawnCursor)
            {
                Destroy(_spawnCursor);
                _spawnCursor = null;
            }

            // Restore or (re)acquire the correct follow target and ensure iso cam is enabled.
            if (_isoCam)
            {
                Transform target = _originalFollow;

                // Prefer the currently spawned local player if available.
                var nm = Unity.Netcode.NetworkManager.Singleton;
                var po = nm ? nm.LocalClient?.PlayerObject : null;
                if (po && po.GetComponent<PlayerNetwork>())
                    target = po.transform;

                _isoCam.follow = target;    // may be new spawn or the previous follow
                _isoCam.enabled = true;
            }
        }
// Now we always reattach the iso camera to the current local PlayerObject (covers late spawn/random cases).

        // Apply or restore transparency on configured objects
        void ApplySelectTransparency(bool on)
        {
            if (fadeDuringSelect == null) return;
            for (int i = 0; i < fadeDuringSelect.Length; i++)
            {
                var r = fadeDuringSelect[i];
                if (!r) continue;

                if (on)
                {
                    if (!_origFadeColors.ContainsKey(r))
                    {
                        var mats = r.materials;
                        var colors = new Color[mats.Length];
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            Color c = Color.white;
                            if (mat.HasProperty("_BaseColor")) c = mat.GetColor("_BaseColor");
                            else if (mat.HasProperty("_Color")) c = mat.GetColor("_Color");
                            colors[m] = c;

                            // Switch URP Lit to Transparent if currently Opaque.
                            TryMakeTransparent(mat, true);
                        }
                        _origFadeColors[r] = colors;
                    }

                    var matsNow = r.materials;
                    for (int m = 0; m < matsNow.Length; m++)
                    {
                        var mat = matsNow[m];
                        if (mat.HasProperty("_BaseColor"))
                        {
                            var c = mat.GetColor("_BaseColor"); c.a = selectFadeAlpha; mat.SetColor("_BaseColor", c);
                        }
                        else if (mat.HasProperty("_Color"))
                        {
                            var c = mat.GetColor("_Color"); c.a = selectFadeAlpha; mat.SetColor("_Color", c);
                        }
                        TryMakeTransparent(mat, true);
                    }
                }
                else
                {
                    if (_origFadeColors.TryGetValue(r, out var colors))
                    {
                        var mats = r.materials;
                        for (int m = 0; m < mats.Length && m < colors.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", colors[m]);
                            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", colors[m]);

                            // Restore to Opaque if we flipped it.
                            TryMakeTransparent(mat, false);
                        }
                    }
                }
            }

            if (!on) _origFadeColors.Clear();
        }

        // Minimal URP Lit toggle: Opaque<->Transparent
        static void TryMakeTransparent(Material mat, bool on)
        {
            if (!mat) return;

            // URP Lit: _Surface 0=Opaque, 1=Transparent
            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", on ? 1f : 0f);

            // Common blending toggles
            if (on)
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                mat.SetInt("_ZWrite", 1);
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = -1; // let shader decide
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

        static TeamId OpposingTeam(TeamId team) => team == TeamId.A ? TeamId.B : TeamId.A;

        static Transform FindDeep(Transform root, string name)
        {
            if (!root || string.IsNullOrEmpty(name)) return null;
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all) if (t && t.name == name) return t;
            return null;
        }

        // Top-down orthographic/perspective frame of the full map bounds with a margin.
        // Fix: respect camera aspect so the whole map fits with extra margin (no cut-off).
        void FrameSpawnCameraFullMap()
        {
            if (!AcquireCameraSafe()) return;
            var map = areas ? areas.GetMapBounds() : new Bounds(Vector3.zero, new Vector3(60, 0, 60));
            var center = map.center;
            float halfW = map.extents.x * spawnCamMargin;
            float halfH = map.extents.z * spawnCamMargin;

            // Top-down position & rotation
            var pos = new Vector3(center.x, spawnCamHeight, center.z);
            var rot = Quaternion.Euler(90f, 0f, 0f);

            if (spawnCamUseOrthographic)
            {
                _cam.orthographic = true;
                // Orthographic size is vertical half-size. Fit width using aspect.
                float sizeY = Mathf.Max(halfH, halfW / Mathf.Max(0.0001f, _cam.aspect));
                _cam.orthographicSize = sizeY;
            }
            else
            {
                _cam.orthographic = false;
                // Perspective fit: satisfy both vertical and horizontal fits.
                float fovRad = Mathf.Deg2Rad * Mathf.Clamp(_cam.fieldOfView, 1f, 179f);
                float needY = halfH / Mathf.Tan(fovRad * 0.5f);
                float needX = (halfW / Mathf.Max(0.0001f, _cam.aspect)) / Mathf.Tan(fovRad * 0.5f);
                float need = Mathf.Max(needX, needY);
                pos.y = Mathf.Max(spawnCamHeight, need);
            }

            _cam.transform.SetPositionAndRotation(pos, rot);
        }

        // Helper: get precise ground height for cursor placement (uses the same ground mask)
        float GroundYAt(Vector3 xz)
        {
            var origin = new Vector3(xz.x, spawnCamHeight + 500f, xz.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, 5000f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return xz.y;
        }
// Camera framing now fits full map with margin even on ultra-wide/tall aspects.



        // Ensure spawn cursor exists and starts hidden.
        void EnsureSpawnCursor()
        {
            if (!_spawnCursor)
            {
                if (spawnCursorPrefab) _spawnCursor = Instantiate(spawnCursorPrefab);
                else
                {
                    _spawnCursor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    _spawnCursor.transform.localScale = new Vector3(1f, 0.2f, 1f);
                    Destroy(_spawnCursor.GetComponent<Collider>());
                    var rend = _spawnCursor.GetComponent<Renderer>();
                    if (rend) rend.material.color = Color.green;
                }
                foreach (var col in _spawnCursor.GetComponentsInChildren<Collider>(true))
                    Destroy(col);
                int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
                if (ignoreRaycast >= 0) _spawnCursor.layer = ignoreRaycast;
            }
            if (_spawnCursor) _spawnCursor.SetActive(false);
        }

        // Remove any ship/stand-in leftovers and hide real player visuals until actual spawn.
        void ForceClearCinematicResidueLocal()
        {
            // Detach cam if it’s under the ship
            DetachCameraFromShip();

            if (_shipInstance) { Destroy(_shipInstance); _shipInstance = null; }

            var anchors = UnityEngine.Object.FindObjectsByType<PlayerVisualAnchor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < anchors.Length; i++)
            {
                var a = anchors[i];
                if (!a) continue;
                a.DetachToWorld(true);     // ensure no parenting remains
                a.SetModelVisible(false);  // hide until actual spawn
            }
        }

        // Prepare for pan: detach camera and hide player models/stand-ins, but keep the ship alive.
        void PrepareCinematicForPanLocal()
        {
            DetachCameraFromShip();

#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var anchors = UnityEngine.Object.FindObjectsByType<PlayerVisualAnchor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            var anchors = UnityEngine.Object.FindObjectsOfType<PlayerVisualAnchor>();
#endif
            for (int i = 0; i < anchors.Length; i++)
            {
                var a = anchors[i];
                if (!a) continue;
                a.DetachToWorld(true);
                a.SetModelVisible(false);
            }
        }

        // Destroy ship and any client-only stand-ins once spawn-select begins.
        void ClearCinematicShip()
        {
            DetachCameraFromShip();
            if (_shipInstance) { Destroy(_shipInstance); _shipInstance = null; }

#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var standIns = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            var standIns = UnityEngine.Object.FindObjectsOfType<Animator>();
#endif
            // Best-effort: remove any loose client-only stand-ins we created (they have no NetworkObject)
            for (int i = 0; i < standIns.Length; i++)
            {
                var anim = standIns[i]; if (!anim) continue;
                var no = anim.GetComponentInParent<Unity.Netcode.NetworkObject>();
                if (no == null)
                {
                    // Destroy only if it was under the ship or still near the ship spawn path
                    if (_shipInstance == null || (anim.transform && !anim.transform.IsChildOf(_shipInstance.transform)))
                        continue;
                    UnityEngine.Object.Destroy(anim.gameObject);
                }
            }
        }

        IEnumerator CoIntroPanThenOpenSpawnUI()
        {
            if (_panCo != null) { StopCoroutine(_panCo); _panCo = null; }
            if (!AcquireCameraSafe()) yield break;

            // temporary disable iso cam while we pan
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (_isoCam == null) _isoCam = UnityEngine.Object.FindFirstObjectByType<IsometricCamera>(FindObjectsInactive.Include);
#else
            if (_isoCam == null) _isoCam = UnityEngine.Object.FindObjectOfType<IsometricCamera>();
#endif
            if (_isoCam) _isoCam.enabled = false;

            // position at start
            _cam.transform.SetParent(null, true);
            _cam.transform.position = mapPanStart ? mapPanStart.position : _cam.transform.position;
            _cam.transform.rotation = mapPanStart ? mapPanStart.rotation : _cam.transform.rotation;

            float t = 0f;
            while (t < 1f)
            {
                if (!AcquireCameraSafe()) yield break;
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, mapPanSeconds);
                _cam.transform.position = Vector3.LerpUnclamped(mapPanStart.position, mapPanEnd.position, t);

                // Always face the spawn-select look target during the pan if assigned
                if (spawnSelectLookTarget)
                {
                    var dir = spawnSelectLookTarget.position - _cam.transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 1e-4f)
                        _cam.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                }
                else
                {
                    _cam.transform.rotation = Quaternion.SlerpUnclamped(mapPanStart.rotation, mapPanEnd.rotation, t);
                }
                yield return null;
            }

// cut to spawn-select framing
// clear ship/stand-ins NOW that spawn-select begins, then frame and show UI
ClearCinematicShip();
FrameSpawnCameraFullMap();
            // Re-send carved holes (guard against blockers changing during pan)
            {
                List<Bounds> holes = null;
                if (areas) holes = areas.GetBlockerIntersectionsFor(_myAreaBounds, null);
                ApplySpawnHighlightLayout(holes);
            }

            // Ensure cursor exists and starts hidden
            EnsureSpawnCursor();

            // Fade configured objects during selection
            ApplySelectTransparency(true);
            _panCo = null;

            ShowCanvas(spawnCanvas, true);
            if (spawnHintText)
            {
                float remain = Mathf.Max(0f, _spawnDeadlineLocal - Time.unscaledTime);
                spawnHintText.text = Mathf.CeilToInt(remain).ToString("0");
            }

            // Start the timer now that UI is open
            if (_selectCo != null) StopCoroutine(_selectCo);
            _selectCo = StartCoroutine(CoSpawnSelectTimer());
        }

        // Ensure a SeatMount exists under the ship and return it.
        Transform EnsureSeatMount(Transform shipRoot, string mountName)
        {
            var t = FindDeep(shipRoot, mountName);
            if (t) return t;

            var go = new GameObject(string.IsNullOrEmpty(mountName) ? "SeatMount" : mountName);
            go.transform.SetParent(shipRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
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
                    ? $"Waiting for {_playerCount.Value}/{requiredPlayers} players"
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

        // Live "players joined" UI update
        [ClientRpc]
        void UpdateRequiredPlayersClientRpc(int joined, int needed)
        {
            if (statusText && statusCanvas && _state.Value == MatchState.Waiting)
            {
                statusText.text = $"Waiting for {joined}/{needed} players";
                ShowCanvas(statusCanvas, true);
            }
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

        [ClientRpc]
        void RefreshUIClientRpc()
        {
            RefreshUI();
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

            // Update texts
            if (scoreTextA) scoreTextA.text = $"Team A: {_winsTeamA.Value}";
            if (scoreTextB) scoreTextB.text = $"Team B: {_winsTeamB.Value}";
            if (roundNumberText)
            {
                string roundText = $"Round {_roundNumber.Value}";
                if (_suddenDeath.Value) roundText += " (SUDDEN DEATH)";
                roundNumberText.text = roundText;
            }

            // Hide score/timer HUD until actual round play begins
            bool showHud = (_state.Value == MatchState.Playing);
            if (roundTimerText)   roundTimerText.gameObject.SetActive(showHud);
            if (scoreTextA)       scoreTextA.gameObject.SetActive(showHud);
            if (scoreTextB)       scoreTextB.gameObject.SetActive(showHud);
            if (roundNumberText)  roundNumberText.gameObject.SetActive(showHud);

            // Status panel gating
            if (_state.Value == MatchState.Waiting)
            {
                ShowCanvas(statusCanvas, true);
                if (statusText)
                    statusText.text = _playerCount.Value >= requiredPlayers
                        ? $"{requiredPlayers} players connected. Match starting..."
                        : $"Waiting for {_playerCount.Value}/{requiredPlayers} players";
            }
            else if (_state.Value == MatchState.Playing)
            {
                ShowCanvas(statusCanvas, false);
            }

            // Update required players UI
            if (_requiredPlayersUI) _requiredPlayersUI.text = requiredPlayers.ToString();
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

        // Safe server clock (works before NGO is listening)
        static float GetServerNowSafe()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return (float)nm.ServerTime.TimeAsFloat;
            return Time.unscaledTime;
        }

        // Unity fake-null safe checks
        static bool IsUnityNull(Object o) => o == null;

        bool AcquireCameraSafe()
        {
            if (!IsUnityNull(_cam)) return true;

            // Prefer the scene's main camera
            _cam = Camera.main;

            // Fallback to IsometricCamera’s Camera
            if (IsUnityNull(_cam))
            {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
                var iso = UnityEngine.Object.FindFirstObjectByType<IsometricCamera>(FindObjectsInactive.Include);
#else
                var iso = UnityEngine.Object.FindObjectOfType<IsometricCamera>();
#endif
                if (iso) _cam = iso.GetComponent<Camera>();
            }

            // Any camera as last resort
            if (IsUnityNull(_cam))
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (cams != null && cams.Length > 0) _cam = cams[0];
            }

            return !IsUnityNull(_cam);
        }

        void DetachCameraFromShip()
        {
            if (IsUnityNull(_cam)) return;
            if (_shipInstance && _cam.transform.IsChildOf(_shipInstance.transform))
                _cam.transform.SetParent(null, true);
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

        void StopCoroutineSafe(ref Coroutine co)
        {
            if (co != null) { StopCoroutine(co); co = null; }
        }

        IEnumerator CoFlashInvalid(TMP_Text t)
        {
            if (!t) yield break;
            string prev = t.text;
            t.text = "Invalid location";
            float dur = 0.5f, e = 0f;
            while (e < dur)
            {
                e += Time.unscaledDeltaTime;
                yield return null;
            }
            t.text = prev;
        }
    }
}
