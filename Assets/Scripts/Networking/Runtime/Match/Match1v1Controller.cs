using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Game.Net; // use existing GroundClampServer
// Resolve type without fully qualifying everywhere.
using UnityEngine;
using TMPro;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Game.Services;

namespace Game.Net
{
    public enum MatchState : byte
    {
        Waiting = 0,
        Countdown = 1,
        FlyIn = 2,
        Playing = 4,
        RoundEnd = 5,
        MatchEnd = 6
    }
// Remove unused SpawnSelect state. Explicit values keep network stability.

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

    [Header("Player Icons")]
    [SerializeField] private Image teamAPlayerIcon;
    [SerializeField] private Image teamBPlayerIcon;
    [SerializeField] private Sprite defaultPlayerIcon;
    [SerializeField] private bool hidePlayerIconWhenMissing = true;

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

// Removed old spawn-select UI and camera fields.

        [Header("Timings")]
        [SerializeField, Min(1f)] private int countdownSeconds = 3;
        [SerializeField, Min(3f)] private float cinematicSeconds = 3.5f;
        [SerializeField, Min(0f)] private float roundDurationSeconds = 90f;

        [Header("Pre-Round")]
        [SerializeField, Min(1)] private int preRoundCountdownSeconds = 3;
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

        Coroutine _cineCo;   // track coroutines so we can stop safely

// Removed map pan used only by selection flow.

// Removed unused spawn-select camera target.

// Removed legacy fade-for-selection fields.

// Cache for restore after select
        bool _preSelectOrtho;
        float _preSelectOrthoSize;

// Removed legacy spawn-select framing settings.

        private readonly NetworkVariable<MatchState> _state = new();
        private readonly NetworkVariable<int> _playerCount = new();
        private readonly NetworkVariable<int> _roundNumber = new();
        private readonly NetworkVariable<float> _roundEndTime = new();
        private readonly NetworkVariable<int> _winsTeamA = new();
        private readonly NetworkVariable<int> _winsTeamB = new();
        private readonly NetworkVariable<bool> _suddenDeath = new();

        // Server state
        private readonly Dictionary<ulong, TeamId> _teams = new();
        // removed _chosenSpawns
        // removed _spawnDeadlineServer
        private bool _firstRound = true;
        private float _roundStartTimeServer;

        // Client state
        // removed _selecting, _myAreaBounds, _myAreaBlocked, _myTeam, _spawnDeadlineLocal, _selectCo, _spawnCursor
    private Coroutine _flyCo, _uiCo, _iconWarmupCo;
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

                // Install LOS system on server.
                LosVisibilitySystem.Install(
                    networkManager: NetworkManager,
                    losRadiusMeters: 12f,
                    fadeSeconds: 1f,
                    occluderMask: LayerMask.GetMask("Occluder", "OccluderExtra")
                );
            }

            _state.OnValueChanged += (_, __) => RefreshUI();
            _playerCount.OnValueChanged += (_, __) => RefreshUI();
            _roundNumber.OnValueChanged += (_, __) => RefreshUI();
            _winsTeamA.OnValueChanged += (_, __) => RefreshUI();
            _winsTeamB.OnValueChanged += (_, __) => RefreshUI();

            if (IsClient)
            {
                var identityTask = Game.Services.PlayerIdentityState.EnsureIdentityAsync();

                _cam = Camera.main;
                if (_cam) _isoCam = _cam.GetComponent<IsometricCamera>();
                RefreshUI();

                if (returnToLobbyButton)
                    returnToLobbyButton.onClick.AddListener(OnReturnToLobby);

                StopCoroutineSafe(ref _iconWarmupCo);
                _iconWarmupCo = StartCoroutine(CoAwaitIdentity(identityTask));

                RefreshAlwaysOnIcons();
                InvokeRepeating(nameof(RefreshAlwaysOnIcons), 0.5f, 1.0f);
            }
        }

        public override void OnNetworkDespawn()
        {
            StopCinematicClientRpc();
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            if (returnToLobbyButton)
                returnToLobbyButton.onClick.RemoveListener(OnReturnToLobby);

            if (IsClient)
            {
                CancelInvoke(nameof(RefreshAlwaysOnIcons));
                StopCoroutineSafe(ref _iconWarmupCo);
            }

            if (IsServer) LosVisibilitySystem.Shutdown();
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
            // removed _chosenSpawns.Remove(clientId);
            RecountPlayers();
            UpdateRequiredPlayersClientRpc(_playerCount.Value, requiredPlayers);

            if (_playerCount.Value < 2 && _state.Value != MatchState.MatchEnd)
            {
                _state.Value = MatchState.Waiting;
                BroadcastPauseAll(true);
                // removed _chosenSpawns.Clear();
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

            yield return StartRound();
        }

        IEnumerator StartRound()
        {
            StopCinematicClientRpc();
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

            // No spawn-choose: immediately spawn both teams at random side points, then do a 3..2..1 and start.
            SpawnAllAndStartRound();
        }

        [ClientRpc]
        void StartCinematicClientRpc()
        {
            if (!IsClient) return;
            if (_state.Value != MatchState.FlyIn) return;        // hard guard
            if (_shipInstance) return;                           // do not replay if ship exists
            if (_cineCo != null) StopCoroutine(_cineCo);
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

        void Update()
        {
            // Avoid nulls before NGO starts
            if (!Application.isPlaying) return;

            // removed spawn select logic

            if (IsClient && _state.Value == MatchState.Playing && roundTimerText)
            {
                var serverNow = GetServerNowSafe();
                var t = Mathf.Max(0f, _roundEndTime.Value - serverNow);
                int minutes = (int)(t / 60);
                int seconds = (int)(t % 60);
                roundTimerText.text = $"{minutes:0}:{seconds:00}";
            }
        }

        void SpawnAllAndStartRound()
        {
            if (!IsServer) return;

            if (!playerPrefab)
            {
                Debug.LogError("[Match1v1] Player prefab not assigned on controller.");
                return;
            }

            // Spawn fresh PlayerObjects at random fixed points (fallback to unblocked area sample).
            var ids = NetworkManager.ConnectedClientsIds;
            foreach (var cid in ids)
            {
                if (cid == NetworkManager.ServerClientId) continue;
                if (!_teams.TryGetValue(cid, out var team)) continue;

                Vector3 point = transform.position;

                if (areas)
                {
                    // Prefer designer-defined side arrays; fallback to random unblocked inside team bounds.
                    if (!areas.TryGetRandomFixedSpawn(team, out point))
                        point = areas.GetRandomUnblockedPoint(team, 128);
                }

                SpawnFreshPlayerForClient(cid, point, team);
            }

            // Keep players frozen/paused and visible while we run the 3..2..1 pre-round countdown.
            SetAllPlayersVisibleClientRpc(true);

            FreezeAllPlayers(true);
            BroadcastPauseAll(true);

            StopCoroutineSafe(ref _uiCo);
            StartCoroutine(CoPreRoundCountdownThenStart());
        }

        IEnumerator CoPreRoundCountdownThenStart()
        {
            _state.Value = MatchState.Countdown;
            for (int i = preRoundCountdownSeconds; i > 0; i--)
            {
                CountdownClientRpc(i);
                yield return new WaitForSecondsRealtime(1f);
            }
            CountdownClientRpc(0);
            yield return new WaitForSecondsRealtime(0.25f);

            _state.Value = MatchState.Playing;
            // server-authoritative start/end; guard if server time not ready yet
            var now = GetServerNowSafe();
            _roundStartTimeServer = now;
            _roundEndTime.Value   = now + roundDurationSeconds;

            SetAllPlayersVisibleClientRpc(true);
            FreezeAllPlayers(false);
            BroadcastPauseAll(false);

// After Cloud Save round-trip during countdown, force a clean equip using the now-populated loadout.
// This drives HUD (weapon name, ammo, reload icon) to update immediately when the round starts.
ReequipAllPlayersServer();

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

            // Prefabs must be pre-registered in NetworkManager; never add at runtime (can corrupt NGO tables).
            inst.SpawnAsPlayerObject(clientId);

            // Register with LOS server culling.
            LosVisibilitySystem.Instance?.RegisterTarget(inst, team);

            // Initialize team and health post-spawn.
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn)
            {
                pn.SetTeam(team);
                pn.SetHealth(100f);
                // Defer equip until round actually starts (see ReequipAllPlayersServer).
                // This ensures Cloud Save loadout is present before the first equip drives the HUD.
                pn.ClearDeathRecapForOwner();
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

                if (roundWinner != -1 || GetServerNowSafe() >= _roundEndTime.Value)
                {
                    EndRound(roundWinner);
                    yield break;
                }

                // Tighter polling so the round ends exactly at 0:00 (≤50ms jitter).
                yield return new WaitForSeconds(0.05f);
            }
        }

        // Use server clock consistently and increase polling rate so the round flips precisely at 0:00.

        void EndRound(int winnerTeam)
        {
            _state.Value = MatchState.RoundEnd;

            if (winnerTeam == 0) _winsTeamA.Value++;
            else if (winnerTeam == 1) _winsTeamB.Value++;
            else { _winsTeamA.Value++; _winsTeamB.Value++; } // draw => both get a point

            ShowRoundEndClientRpc(winnerTeam);
            ClearDeathRecapsClientRpc();
            StartCoroutine(CoPostRoundFlow());
        }

        IEnumerator CoPostRoundFlow()
        {
            StopCinematicClientRpc();
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
            StopCinematicClientRpc();
            _state.Value = MatchState.MatchEnd;
            ClearDeathRecapsClientRpc();
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
        void ClearDeathRecapsClientRpc()
        {
            PlayerNetwork.InvokeHideDeathRecap();
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

        // Apply or restore transparency on configured objects
        void ApplySelectTransparency(bool on)
        {
            // Removed legacy fade-for-selection logic.
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

        // Helper: get precise ground height for cursor placement (uses the same ground mask)
        float GroundYAt(Vector3 xz)
        {
            var origin = new Vector3(xz.x, 80f + 500f, xz.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, 5000f, groundMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return xz.y;
        }
// Camera framing now fits full map with margin even on ultra-wide/tall aspects.



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

/// <summary>Client-side cinematic cleanup RPC and local helper.</summary>
[ClientRpc]
void StopCinematicClientRpc()
{
    if (!IsClient) return;
    CleanupCinematicLocal(restoreCamera:true);
}

void CleanupCinematicLocal(bool restoreCamera)
{
    if (_cineCo != null) { StopCoroutine(_cineCo); _cineCo = null; }
    DetachCameraFromShip();
    if (_shipInstance) { Destroy(_shipInstance); _shipInstance = null; }
    if (_isoCam && restoreCamera) _isoCam.enabled = true;
}

/// <summary>Detach main camera from ship if it was parented.</summary>
void DetachCameraFromShip()
{
    if (!_cam) _cam = Camera.main;
    if (!_cam) return;
    // If camera is parented under the ship, detach and restore last known transform.
    if (_cam.transform && _cam.transform.parent != null && _shipInstance && _cam.transform.IsChildOf(_shipInstance.transform))
    {
        _cam.transform.SetParent(null, worldPositionStays:true);
        _cam.transform.SetPositionAndRotation(_preCinematicCamPos, _preCinematicCamRot);
    }
}
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
            if (players == null || players.Length == 0) return;

            PlayerNetwork local = null;
            for (int i = 0; i < players.Length; i++)
            {
                var pl = players[i];
                if (!pl) continue;
                if (pl.IsOwner)
                {
                    local = pl;
                    break;
                }
            }

            var localTeam = local ? local.GetTeam() : TeamId.A;

            for (int i = 0; i < players.Length; i++)
            {
                var pl = players[i];
                if (!pl) continue;

                bool sameTeam = local && pl.GetTeam() == localTeam;
                bool affect = pl.IsOwner || sameTeam;

                if (visible)
                {
                    if (affect) pl.SetVisible(true);
                }
                else
                {
                    pl.SetVisible(false);
                }
            }
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

            RefreshAlwaysOnIcons();
        }

        void RefreshAlwaysOnIcons()
        {
            if (!IsClient) return;

            Sprite iconA = null;
            Sprite iconB = null;

            // Drive icons from connected clients (persistent), not from spawned PlayerNetwork instances (LOS can despawn).
            var ids = NetworkManager.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
            {
                var cid = ids[i];
                if (cid == NetworkManager.ServerClientId) continue;

                // Resolve team from the authoritative team map if available, otherwise fall back to live object (if present)
                TeamId team;
                if (!_teams.TryGetValue(cid, out team))
                {
                    var pn = NetworkManager.ConnectedClients.TryGetValue(cid, out var cc)
                        ? cc.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null
                        : null;
                    team = pn ? pn.GetTeam() : TeamId.A;
                }

                // Use last-known icon id cached in PlayerNetwork (persists across despawns)
                var iconId = PlayerNetwork.GetCachedIconId(cid);
                var sprite = ProfileIconLookup.Resolve(iconId);
                if (!sprite) sprite = defaultPlayerIcon;

                switch (team)
                {
                    case TeamId.A:
                        if (iconA == null) iconA = sprite;
                        break;
                    case TeamId.B:
                        if (iconB == null) iconB = sprite;
                        break;
                }
            }

            ApplyAlwaysOnIcon(teamAPlayerIcon, iconA);
            ApplyAlwaysOnIcon(teamBPlayerIcon, iconB);
        }
// Icons now ignore LOS-culling; they only change when a client connects/disconnects or identity changes.

        IEnumerator CoAwaitIdentity(Task identityTask)
        {
            if (identityTask == null) yield break;
            while (!identityTask.IsCompleted)
                yield return null;

            // Consume exception to avoid unobserved fault, but continue refreshing icons.
            if (identityTask.IsFaulted && identityTask.Exception != null)
            {
                Debug.LogWarning($"[Match1v1] Identity preload failed: {identityTask.Exception.GetBaseException().Message}");
            }

            RefreshAlwaysOnIcons();
        }

        void ApplyAlwaysOnIcon(Image target, Sprite sprite)
        {
            if (!target) return;
            if (sprite)
            {
                target.sprite = sprite;
                target.enabled = true;
                return;
            }

            if (defaultPlayerIcon)
            {
                target.sprite = defaultPlayerIcon;
                target.enabled = true;
            }
            else
            {
                target.enabled = !hidePlayerIconWhenMissing;
                if (!target.enabled)
                    target.sprite = null;
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
                    // Unregister from LOS before destroying.
                    LosVisibilitySystem.Instance?.UnregisterTarget(po);

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
        public void UpdatePlayerHealth(ulong clientId, float healthDelta, ulong attackerClientId = ulong.MaxValue)
        {
            if (!IsServer) return;
            var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var cc) ? cc.PlayerObject?.GetComponent<PlayerNetwork>() : null;
            if (player == null) return;
            PlayerNetwork killer = null;
            if (attackerClientId != ulong.MaxValue && NetworkManager.ConnectedClients.TryGetValue(attackerClientId, out var killerClient))
                killer = killerClient.PlayerObject ? killerClient.PlayerObject.GetComponent<PlayerNetwork>() : null;

            player.ApplyHealthDelta(healthDelta, killer);
        }

        void StopCoroutineSafe(ref Coroutine co)
        {
            if (co != null) { StopCoroutine(co); co = null; }
        }

/// <summary>
/// Re-equip Primaries for all players at round start, after Cloud Save loadouts have replicated.
/// Safe to call each round; idempotent on the server-side equip code.
/// </summary>
void ReequipAllPlayersServer()
{
    if (!IsServer) return;
    foreach (var cc in NetworkManager.ConnectedClientsList)
    {
        if (cc.ClientId == NetworkManager.ServerClientId) continue;
        var pn = cc.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null;
        if (pn) pn.ServerAutoEquipPrimary();
    }
}
    }

    // moved into Match1v1Controller class
}
