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
        Loading = 2, // repurpose old value 2 (was FlyIn) to keep NV packing stable
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

    // Cinematic removed.

// Removed old spawn-select UI and camera fields.

    [Header("Timings")]
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
    private float _roundStartTimeServer;

        // --- Loading phase readiness (server) ---
        [System.Flags]
        private enum LoadingReady : byte { None = 0, Phase = 1, Los = 2, Loadout = 4 }
        private readonly Dictionary<ulong, LoadingReady> _loadingReady = new();

        // Client state
        // removed _selecting, _myAreaBounds, _myAreaBlocked, _myTeam, _spawnDeadlineLocal, _selectCo, _spawnCursor
    private Coroutine _uiCo, _iconWarmupCo, _loadingDotsCo;

    // Cinematic fields removed.

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                EnsureServerLoadoutHandshake();

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
            yield return StartRound();
        }

        IEnumerator StartRound()
        {
            // Halftime side swap: from round > ceil(winsNeeded/2) we flip Team A/B spawn sides.
            if (areas)
            {
                bool swap = _roundNumber.Value > Mathf.CeilToInt(winsNeeded / 2f);
                areas.SetSwapSides(swap);
            }

            // Spawn both players immediately, then run the pre-round 3..2..1 and start.
            SpawnAllAndStartRound();
            yield break;
        }


/* Start is now straight to spawn+countdown; no pre-cinematic countdown, no fly-in. */

        // removed
        // Brief dev comment: Client cinematic entrypoint no longer needed.

        // removed
        // Brief dev comment: Entire fly-in coroutine deleted.

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

            // Spawned and frozen/paused/visible while we run the LOADING phase, then the 3..2..1.
            SetAllPlayersVisibleClientRpc(true);
            FreezeAllPlayers(true);
            BroadcastPauseAll(true);

            StopCoroutineSafe(ref _uiCo);
            StartCoroutine(CoLoadingPhaseThenCountdown());
        }


        // We now enter a Loading phase first; countdown only starts once both players are ready.

        IEnumerator CoPreRoundCountdownThenStart()
        {
            _state.Value = MatchState.Countdown;

            // Equip during countdown (server authoritative), primary auto-equipped
            ReequipAllPlayersServer();

            for (int i = preRoundCountdownSeconds; i > 0; i--)
            {
                CountdownClientRpc(i);
                yield return new WaitForSecondsRealtime(1f);
            }
            CountdownClientRpc(0);
            yield return new WaitForSecondsRealtime(0.25f);

            _state.Value = MatchState.Playing;
            var now = GetServerNowSafe();
            _roundStartTimeServer = now;
            _roundEndTime.Value   = now + roundDurationSeconds;

            SetAllPlayersVisibleClientRpc(true);
            FreezeAllPlayers(false);
            BroadcastPauseAll(false);

            TopUpAndSanitizeAllPlayersServer();
            // Re-equip not needed here anymore; already done at countdown start.

            StartCoroutine(CoMonitorRound());
        }


        // Primary is now in-hand throughout the 3-second countdown; players unfreeze at 0.

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
            var pn = inst.GetComponent<PlayerNetwork>();
            if (pn) pn.SeedPhasePreSpawnServer(PlayerPhase.Match);
            inst.SpawnAsPlayerObject(clientId);
            // Brief dev comment: Pre-seed phase to Match before spawn; clients no longer log "Lobby" on spawn.

            // Register with LOS server culling.
            LosVisibilitySystem.Instance?.RegisterTarget(inst, team);

            // Initialize team and health post-spawn.
            if (pn)
            {
                pn.SetTeam(team);
                pn.SetHealth(100f);

                // Replicate phase -> Match so clients enable Match systems.
                pn.SetPhaseServerRpc(PlayerPhase.Match);

                // Seed authoritative loadout if the client sent it during connection.
                if (LoadoutHandshake.TryGetPreJoinLoadout(clientId, out var pre))
                {
                    Debug.Log($"[Match1v1] Applying pre-join loadout for cid={clientId}: P={pre.primary} S={pre.secondary} M={pre.melee} U={pre.util}");
                    pn.ApplyPreJoinLoadoutServer(pre.primary, pre.secondary, pre.melee, pre.util);
                    LoadoutHandshake.Consume(clientId);
                }

                // Mark PHASE bit ready immediately (phase is server-driven).
                OnClientLoadingBits(clientId, (byte)LoadingReady.Phase);

                // Ask the owner to enable LOS visuals and fetch their Cloud Save loadout, then ack.
                pn.BeginLoadingPhaseClientRpc(PlayerNetwork.TargetClientParams(clientId));

                pn.ClearDeathRecapForOwner();
            }
// Brief dev comment: Reuse pn declared before spawn.
// Spawner now seeds the player's NetLoadout from the pre-join cache, eliminating desync.
// Players spawn already holding Primary.
        }

    void EnsureServerLoadoutHandshake()
    {
        if (!IsServer) return;

#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        var existing = FindFirstObjectByType<LoadoutHandshake>(FindObjectsInactive.Include);
#else
        var existing = FindObjectOfType<LoadoutHandshake>();
#endif
        if (existing != null) return;

        var go = new GameObject("LoadoutHandshake(Server)");
        DontDestroyOnLoad(go);
        go.AddComponent<LoadoutHandshake>();
        Debug.Log("[Match1v1] Spawned server LoadoutHandshake");
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



        // removed
        // Brief dev comment: No ship/stand-in leftovers to clear; cinematic removed.

        // removed
        // Brief dev comment: Selection/cinematic prep removed.

        // removed
        // Brief dev comment: Ship object is no longer spawned, so no cleanup needed.

// removed
// Brief dev comment: No ship/camera parenting, so cleanup path is gone.

// removed
// Brief dev comment: No cinematic camera parenting remains.
        // removed
        // Brief dev comment: Seat mount helper was only used by the cinematic ship.

        void FreezeAllPlayers(bool frozen)
        {
            Debug.Log($"[Match1v1] FreezeAllPlayers -> {frozen} count={NetworkManager.ConnectedClientsList.Count}");
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
            Debug.Log($"[Match1v1] BroadcastPauseAll -> {paused} recipients={NetworkManager.ConnectedClientsIds.Count-1}");
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
            int count = players != null ? players.Length : 0;
            Debug.Log($"[Match1v1] SetAllPlayersVisible -> {visible} playerObjs={count}");
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

        // removed
        // Brief dev comment: No more cinematic camera choreography.

        static ClientRpcParams ToClient(ulong clientId) =>
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };

        // Despawn all current PlayerObjects. Called after fly-in and between rounds.
        void DespawnAllPlayersServer()
        {
            if (!IsServer) return;
            int despawned = 0;

            foreach (var cc in NetworkManager.ConnectedClientsList)
            {
                if (cc.ClientId == NetworkManager.ServerClientId) continue;
                var po = cc.PlayerObject;
                if (po && po.IsSpawned)
                {
                    // Unregister from LOS before destroying.
                    LosVisibilitySystem.Instance?.UnregisterTarget(po);

                    po.Despawn(true); // destroy object on all peers
                    despawned++;
                }
            }
            Debug.Log($"[Match1v1] DespawnAllPlayersServer count={despawned}");
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
/// Round start hardening: ensure valid loadouts, equip, and reset health.
/// Idempotent on server (safe to call multiple times).
/// </summary>
void ReequipAllPlayersServer()
{
    if (!IsServer) return;
    int count = 0;
    foreach (var cc in NetworkManager.ConnectedClientsList)
    {
        if (cc.ClientId == NetworkManager.ServerClientId) continue;
        var pn = cc.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null;
        if (!pn) continue;

        // Force healthy & ready.
        pn.SetHealth(100f);
        pn.ServerEnsureLoadoutValidAndEquip();
        count++;
    }
    // Force everyone to primary after (re)equip so owner OnActiveSlotChanged runs and HUD updates.
#if UNITY_6000_0_OR_NEWER
    var players = UnityEngine.Object.FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#elif UNITY_2022_3_OR_NEWER
    var players = UnityEngine.Object.FindObjectsOfType<PlayerNetwork>(false);
#else
    var players = UnityEngine.Object.FindObjectsOfType<PlayerNetwork>();
#endif

foreach (var pn in players)
    if (pn && pn.IsSpawned) pn.ForceActiveSlotServer(0);
// Use the right API per version: Unity 6 => FindObjectsByType, 2022.3 => FindObjectsOfType(false).    Debug.Log($"[Match1v1] ReequipAllPlayersServer applied to {players.Length} players");
}

// Brief dev comment: server sets the NV so the client equips Primary, fixes "pistol stuck" and restores weapon name/ammo HUD.
void TopUpAndSanitizeAllPlayersServer()
{
    if (!IsServer) return;
    int count = 0;
    foreach (var cc in NetworkManager.ConnectedClientsList)
    {
        if (cc.ClientId == NetworkManager.ServerClientId) continue;
        var pn = cc.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null;
        if (!pn) continue;
        // Guarantee: full health + non-None loadout before round starts.
        pn.ServerEnsureValidLoadoutAndHealth(sanitizeOnly: false);
        count++;
    }
    Debug.Log($"[Match1v1] TopUpAndSanitizeAllPlayersServer applied to {count} players");
}

// Now guarantees both players start healthy and equipped each round.

        IEnumerator CoLoadingPhaseThenCountdown()
        {
            if (!IsServer) yield break;

            _state.Value = MatchState.Loading;
            SetLoadingUiClientRpc(true); // start “Loading…” dots on all clients

            // Reset server-side readiness map for connected non-server clients
            _loadingReady.Clear();
            var ids = NetworkManager.ConnectedClientsIds;
            foreach (var cid in ids)
            {
                if (cid == NetworkManager.ServerClientId) continue;
                _loadingReady[cid] = LoadingReady.None; // Phase bit is set as each player is spawned
            }

            // Wait until both players report LOS+Loadout ready AND server can see a valid loadout applied.
            bool AllReady()
            {
                foreach (var kvp in _loadingReady)
                {
                    var cid = kvp.Key;
                    var bits = kvp.Value;

                    if ((bits & (LoadingReady.Los | LoadingReady.Loadout)) != (LoadingReady.Los | LoadingReady.Loadout))
                        return false;

                    // Defensive: ensure server has an applied loadout for this player too
                    var cc = NetworkManager.ConnectedClients[cid];
                    var pn = cc?.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null;
                    if (!pn || !pn.ServerHasValidLoadout()) return false;
                }
                return _loadingReady.Count >= requiredPlayers;
            }

            // Poll at NGO tick rate; avoid tight loop.
            while (!AllReady())
                yield return null;

            // Begin 3..2..1 and equip during countdown.
            SetLoadingUiClientRpc(false);
            StartCoroutine(CoPreRoundCountdownThenStart());
        }

        // Called by PlayerNetwork.ServerRpc when a client finishes a loading step.
        internal void OnClientLoadingBits(ulong clientId, byte bits)
        {
            if (!IsServer) return;
            if (!_loadingReady.ContainsKey(clientId)) _loadingReady[clientId] = LoadingReady.None;
            _loadingReady[clientId] |= (LoadingReady)bits;
        }

        // Toggle the “Loading…” animated UI on clients.
        [ClientRpc]
        void SetLoadingUiClientRpc(bool on)
        {
            if (!IsClient) return;

            StopCoroutineSafe(ref _loadingDotsCo);
            if (on)
            {
                _loadingDotsCo = StartCoroutine(CoAnimateLoadingDots());
            }
            else
            {
                if (roundTimerText) roundTimerText.text = string.Empty;
            }
        }

        IEnumerator CoAnimateLoadingDots()
        {
            int dots = 0;
            while (true)
            {
                if (roundTimerText)
                    roundTimerText.text = "Loading" + new string('.', dots);

                dots = (dots + 1) % 4;
                yield return new WaitForSecondsRealtime(0.35f);
            }
        }

        // Server waits on per-player readiness; clients render animated “Loading…”.
}
}


