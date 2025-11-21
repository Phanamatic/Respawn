// Assets/Scripts/Networking/Runtime/Gameplay/PlayerNetwork.cs
// Server-auth movement with stamina + dash, interpolation for remotes,
// plus freeze/visibility controls used by Match1v1Controller.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement; // allow server to check active scene name
using TMPro;
using Game.Net.Weapons;
using Unity.Collections;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using Game.Services;

namespace Game.Net
{
    public enum PlayerPhase : byte { Lobby = 0, Match = 1 }

    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed partial class PlayerNetwork : NetworkBehaviour
    {
        // ---- NetworkVariable layout contract ----
        // Bump when you add/remove/reorder any NetworkVariable fields in this class.
        // Server will broadcast this to clients at spawn; a mismatch means mixed builds.
        private const int NETWORK_LAYOUT_VERSION = 3;

        // Loading-phase flags reported back to server (match controller).
        [System.Flags]
        public enum LoadingReady : byte { None = 0, Phase = 1, Los = 2, Loadout = 4 }

        /* Expose a tiny flags enum so both ends speak the same bit language. */

        [ClientRpc]
        private void AssertLayoutVersionClientRpc(int serverVersion, ClientRpcParams rpcParams = default)
        {
            if (serverVersion != NETWORK_LAYOUT_VERSION)
            {
                Debug.LogError($"[DirectNet] NetworkVariable layout mismatch. Server={serverVersion} Client={NETWORK_LAYOUT_VERSION}. Update all builds.");
                // Optionally: NetworkManager.Singleton?.Shutdown();
            }
        }
        // Brief dev comment: Early, human-readable failure instead of a buffer overflow when deltas arrive.

        // Keep this declared once and at the top so the NetworkVariable order is identical on all builds.
        private readonly NetworkVariable<Game.Net.NetLoadout> _netLoadout =
            new NetworkVariable<Game.Net.NetLoadout>(
                new Game.Net.NetLoadout(),
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server
            );

        // Replicate the currently equipped slot (0=Primary,1=Secondary,2=Melee,3=Utility)
        private readonly NetworkVariable<byte> _activeSlot =
            new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Local cache for quick checks
        Game.Net.PlayerLoadout _myLoadout = Game.Net.PlayerLoadout.Default;
        PlayerPhase _phase = PlayerPhase.Lobby;

        // Replicated phase so clients are told when we've transitioned into Match.
        readonly NetworkVariable<PlayerPhase> _netPhase =
            new NetworkVariable<PlayerPhase>(
                PlayerPhase.Match,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // Guard to avoid duplicate Cloud Save requests.
        bool _loadoutRequested;

        void OnPhaseNetChanged(PlayerPhase oldPhase, PlayerPhase newPhase)
        {
            _phase = newPhase;
            if (IsOwner)
            {
                // Turn FOV/LOS on only in Match, off in Lobby
                SetFovAndLosEnabled(newPhase == PlayerPhase.Match);

                // Always keep camera occluder transparency on in both phases.
                // Lobby uses only the "Occluder" layer; Match uses Occluder + OccluderExtra.
                EnsureCameraOccluderTransparencyActive(newPhase == PlayerPhase.Lobby);
            }

            if (IsOwner && newPhase == PlayerPhase.Match)
            {
                if (!_loadoutRequested)
                {
                    // Skip client-side loadout send in Match; server already applied pre-join.
                    Debug.Log("[PlayerNetwork] Phase changed to Match; pre-join loadout already applied.");
                    _loadoutRequested = true;
                }
                OnActiveSlotChanged();
            }
        }
// Brief dev comment.
// Brief dev comment: Clients didn’t know when phase changed. This NV tells them and kicks off loadout fetch once.        // Simple server rate-limits
    float _lastSwitchServerTime, _lastThrowServerTime;
    // New slot switch rate-limit timestamp (next allowed server-side switch time)
    float _nextAllowedSlotSwitch;
        const float k_MinSwitchInterval = 0.08f;   // ~10/s
        const float k_MinThrowInterval = 0.5f;    // guard spam
        [Header("Movement")]
        [SerializeField, Min(0f)] float moveSpeed = 8.5f;
        [SerializeField, Min(1f)] float sprintMultiplier = 1.9f;

        [Header("Sprint Stamina")]
        [SerializeField, Min(0.1f)] float sprintStaminaMax = 3.5f;
        [SerializeField, Min(0f)] float sprintDrainPerSec = 1.0f;
        [SerializeField, Min(0f)] float sprintRegenPerSec = 1.0f;
        [SerializeField, Min(0f)] float sprintRegenDelay = 0.35f;

        [Header("Dash (smoothed)")]
        [SerializeField, Min(0.1f)] float dashDistance = 8f;
        [SerializeField, Min(0.05f)] float dashDuration = 0.6f;
        [SerializeField, Min(0f)] float dashCooldown = 0.8f;
        [SerializeField, Min(0f)] float dashInputBuffer = 0.2f;

        [Header("Network Interpolation")]
        [SerializeField, Range(0.05f, 0.5f)] float interpolationDelay = 0.1f;
        [SerializeField, Range(5f, 30f)] float extrapolationLimit = 15f;

        [Header("Ground/Aim")]
        [SerializeField] LayerMask groundMask = ~0;   // used for vertical snap/ground checks
        [SerializeField] LayerMask aimMask = 0;     // legacy ray aim mask (kept as fallback)
        [SerializeField] bool aimUsingScreenSpace = true;  // rotate to face mouse on screen
        [SerializeField] float screenAimMinPixels = 6f;    // deadzone to avoid jitter near player
        [SerializeField] float groundSnapUp = 2.0f;
        [SerializeField] float groundSnapDown = 6.0f;
        [SerializeField] float groundSkin = 0.02f;
        [SerializeField] float rotationLerpSpeed = 10f;

        [Header("Networking")]
        [SerializeField] bool networkSyncYaw = true;          // replicate yaw to others
        [SerializeField] float yawSendRateHz = 60f;           // throttle outgoing owner updates
        [SerializeField] float yawSendThresholdDeg = 0.5f;    // change needed before sending
        private Unity.Netcode.NetworkVariable<float> _netYaw =
            new Unity.Netcode.NetworkVariable<float>(
                0f,
                Unity.Netcode.NetworkVariableReadPermission.Everyone,
                Unity.Netcode.NetworkVariableWritePermission.Owner);
        private float _lastSentYaw;
        private float _nextYawSendTime;
        // Owner drives yaw; others follow _netYaw. Throttled to reduce bandwidth.
    [SerializeField] PlayerPhase initialPhase = PlayerPhase.Match;

        [Header("HUD (scene refs; assign via binder)")]
        [SerializeField] Image sprintFill;
        [SerializeField] TMP_Text sprintLabel;
        [SerializeField] Image dashFill;
        [SerializeField] TMP_Text dashLabel;
        [SerializeField] Image healthFill;
        [SerializeField] TMP_Text healthLabel;

        [Header("Visual Root (optional)")]
        [SerializeField] Transform modelRoot;

        // Expose model renderers for LOS fading
        public System.ReadOnlySpan<Renderer> GetModelRenderersSpan()
        {
            return _renderers != null ? new System.ReadOnlySpan<Renderer>(_renderers) : System.ReadOnlySpan<Renderer>.Empty;
        }

        // Small accessor so the camera can fade the local player's renderers without allocating.

        Rigidbody _rb;
        CapsuleCollider _capsule;

        Renderer[] _renderers;
        Collider[] _colliders;

        CombatStats _statsPendingForCloud;
        CombatStats _statsLastPersisted;
        Coroutine _statsSaveRoutine;
        bool _statsFlushImmediate;

        struct NetworkState { public Vector3 position; public float yaw; public Vector3 velocity; public float timestamp; public bool isDashing; }
        NetworkState[] _stateBuffer = new NetworkState[64];
        int _stateCount;

        // Toggle the old RPC+NetworkVariable replication off by default.
        // We rely on NetworkTransform for movement replication.
        [SerializeField] bool useLegacyStateReplication = false;

        NetworkVariable<Vector3> _netPosition = new();
        // REMOVED duplicate _netYaw; we keep the owner-writable _netYaw declared near the top.
        NetworkVariable<Vector3> _netVelocity = new();
        NetworkVariable<bool> _netIsDashing = new();
        readonly NetworkVariable<FixedString64Bytes> _playerName =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        const int MaxDisplayNameLength = 32;
        readonly NetworkVariable<FixedString128Bytes> _playerIconId =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        // Keep <= FixedString128Bytes capacity; 126 leaves headroom for bookkeeping.
        const int MaxIconIdLength = 126;

        [System.Serializable]
        public struct CombatStats : INetworkSerializable
        {
            public ushort kills;
            public ushort deaths;
            public ushort assists;
            public int damage;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref kills);
                serializer.SerializeValue(ref deaths);
                serializer.SerializeValue(ref assists);
                serializer.SerializeValue(ref damage);
            }
        }

        readonly NetworkVariable<CombatStats> _combatStats =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        [System.Serializable]
        public struct DeathRecapPayload : INetworkSerializable
        {
            public ulong killerClientId;
            public FixedString64Bytes killerName;
            public FixedString128Bytes killerIconId;
            public float damage;
            public byte primary;
            public byte secondary;
            public byte utility;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref killerClientId);
                serializer.SerializeValue(ref killerName);
                serializer.SerializeValue(ref killerIconId);
                serializer.SerializeValue(ref damage);
                serializer.SerializeValue(ref primary);
                serializer.SerializeValue(ref secondary);
                serializer.SerializeValue(ref utility);
            }
        }

    InputActionMap _map;
    InputAction _aMove, _aMouse, _aSprint, _aDash;
    InputAction _aFire, _aReload;
    InputAction _aScoreboard;
        public bool IsSprinting { get; private set; }
        public bool IsDashing { get; private set; }
        public event System.Action<bool> SprintChanged;
        public event System.Action<bool> DashChanged;
        InputAction _aSlot1, _aSlot2, _aSlot3, _aSlot4, _aThrow;
        Vector2 _inMove, _inMouse; bool _inSprint;
        float _targetYaw; // Cached yaw from mouse position
        bool _hasValidYaw; // Track if we have a valid yaw from mouse

        float _stamina; float _sprintRegenResumeAt;

        bool _isDashing; float _dashStartTime, _dashEndTime, _dashReadyAt, _dashYaw; Vector3 _dashDirXZ; float _dashQueuedUntil;

        Camera _cam; IsometricCamera _isoCam; int _camBindTries;
        int _scoreboardBindTries;

        bool _inputPaused;
        bool _frozen;

        MatchScoreboardPanel _scoreboard;
        Coroutine _identitySubmitCo;
        bool _identitySubmitted;

        static readonly Dictionary<ulong, string> s_lastKnownNames = new();
        static readonly Dictionary<ulong, string> s_lastKnownIconIds = new();
        static System.Action<DeathRecapPayload> s_onShowDeathRecap;
        static System.Action s_onHideDeathRecap;

        private readonly NetworkVariable<TeamId> _team = new(TeamId.A);

        private readonly NetworkVariable<float> _health = new(100f);

        public override void OnNetworkSpawn()
        {
            enabled = true;
            if (!_rb)
            {
                _rb = GetComponent<Rigidbody>();
                _capsule = GetComponent<CapsuleCollider>();
            }

            // Broadcast NV layout so mismatched clients shout loudly at connect time.
            if (IsServer)
                AssertLayoutVersionClientRpc(NETWORK_LAYOUT_VERSION);

            // CRITICAL: Set phase FIRST before any other initialization.
            // For Match servers, SeedPhasePreSpawnServer already set _netPhase.Value to Match before spawn.
            // This ensures all subsequent logic (loadout fetch, FOV/LOS, weapon equip) sees the correct phase.
            // IMPORTANT: Use initialPhase, not _netPhase.Value, because NetworkVariables don't replicate until AFTER OnNetworkSpawn.
            _phase = initialPhase;
            _netPhase.OnValueChanged += OnPhaseNetChanged;

            // Ensure we respect the replicated phase immediately on spawn (e.g., Match servers).
            var replicatedPhase = _netPhase.Value;
            if (_phase != replicatedPhase)
            {
                OnPhaseNetChanged(_phase, replicatedPhase);
            }

            _identitySubmitted = false;
            if (_identitySubmitCo != null)
            {
                StopCoroutine(_identitySubmitCo);
                _identitySubmitCo = null;
            }

            _playerName.OnValueChanged += OnPlayerNameValueChanged;
            _playerIconId.OnValueChanged += OnPlayerIconValueChanged;

            // Observe active slot to react locally (e.g., show weapon later)
            _activeSlot.OnValueChanged += (_, __) => OnActiveSlotChanged();
            _netLoadout.OnValueChanged += OnNetLoadoutChanged;
            _health.OnValueChanged += OnHealthChanged;
            _combatStats.OnValueChanged += OnCombatStatsChanged;

            OnNetLoadoutChanged(default, _netLoadout.Value);
// Brief dev comment.

            // Subscribe to yaw updates for remote players
            if (networkSyncYaw)
            {
                _netYaw.OnValueChanged += OnYawChanged;
            }

            // Client: send saved loadout to server after spawn
            // Only pull/send the saved loadout once we are actually in a Match.
            // In Lobby we skip this to avoid nulls and unnecessary traffic.
            // SKIP if server already applied pre-join loadout (prevents overwrite race).
            if (IsOwner && _phase == PlayerPhase.Match && !_loadoutRequested)
            {
                // The match spawner already applied the pre-join loadout server-side.
                // Client only needs to send if no pre-join was available (e.g., reconnect).
                // For now, skip the client-side send entirely in Match spawns since
                // ApplyPreJoinLoadoutServer handles it authoritatively.
                Debug.Log("[PlayerNetwork] Owner spawned in Match; pre-join loadout already applied by server.");
                _loadoutRequested = true; // Mark as handled
            }
            else if (IsOwner && _phase == PlayerPhase.Lobby && !_loadoutRequested)
            {
                // In Lobby, load from Cloud Save and send to server for caching
                _loadoutRequested = true;
                StartCoroutine(CoLoadAndSendLoadout());
            }
            else if (IsOwner)
            {
                Debug.Log("[PlayerNetwork] Owner spawned; loadout already requested.");
            }
            // Defers Cloud Save roundtrip until Match phase to avoid Lobby-time NREs.
            // Brief dev comment: Guard prevents duplicate Cloud Save reads if host-side also calls SetPhase().

            // ~2 ticks of interpolation for remotes
            try
            {
                var tick = (int)(NetworkManager.Singleton ? NetworkManager.Singleton.NetworkConfig.TickRate : 60);
                interpolationDelay = Mathf.Max(2f / Mathf.Max(30, tick), 0.01f);
            }
            catch { /* keep serialized default */ }

            if (IsServer)
            {
                // Ensure authoritative spawn stands on ground and is depenetrated.
                TrySnapToGroundImmediate();
                ResolveInitialPenetration();

                if (_rb)
                {
                    _rb.constraints &= ~(RigidbodyConstraints.FreezeRotationY);
                    _rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                }

                // CRITICAL: Force phase based on server type if not already set correctly.
                // This is a safety net in case SeedPhasePreSpawnServer wasn't called before spawn.
                if ((SessionContext.Type == ServerType.OneVOne || SessionContext.Type == ServerType.TwoVTwo) && initialPhase != PlayerPhase.Match)
                {
                    Debug.LogWarning($"[PlayerNetwork] Match server detected but initialPhase was {initialPhase}. Forcing to Match.");
                    initialPhase = PlayerPhase.Match;
                }
                else if (SessionContext.Type == ServerType.Lobby && initialPhase != PlayerPhase.Lobby)
                {
                    Debug.LogWarning($"[PlayerNetwork] Lobby server detected but initialPhase was {initialPhase}. Forcing to Lobby.");
                    initialPhase = PlayerPhase.Lobby;
                }

                SetPhase(initialPhase);
                if (initialPhase == PlayerPhase.Match)
                {
                    SetVisible(false);
                    SetFrozenServer(true);
                }
                else if (initialPhase == PlayerPhase.Lobby)
                {
                    SetFrozenServer(false);
                }
            }

            if (IsOwner)
            {
                SetupInputAndCamera();
                TryBindScoreboard();
                CacheLocalIdentitySnapshot();

                // NEW: Only enable FOV/LOS in Match
                SetFovAndLosEnabled(_phase == PlayerPhase.Match);

                // Also enable the simple camera occluder transparency in Lobby as well.
                EnsureCameraOccluderTransparencyActive(_phase == PlayerPhase.Lobby);

                BeginSubmitNameRoutine();

                // Force local model fully visible each spawn (guards against any fade components).
                EnsureLocalModelVisible();

                // Ensure primary is equipped on the owning client even if activeSlot started at 0 (no OnValueChanged event).
                // This issues an owner-side Equip() which routes to the server for validation.
                OnActiveSlotChanged();

                // Enforce visibility through transparent occluders.
                var vis = GetComponent<EnsurePlayerVisibleThroughOccluders>();
                if (!vis) vis = gameObject.AddComponent<EnsurePlayerVisibleThroughOccluders>();
                if (modelRoot)
                    vis.GetType().GetField("renderers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                       ?.SetValue(vis, modelRoot.GetComponentsInChildren<Renderer>(true));

                TrySnapToGroundImmediate(); // client visual safety

                if (!GetComponent<NetworkTransform>())
                {
                    var nt = gameObject.AddComponent<NetworkTransform>();
                    nt.SyncPositionX = nt.SyncPositionY = nt.SyncPositionZ = true;
                    nt.SyncRotAngleY = true;
                    nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
                    nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                    nt.UseHalfFloatPrecision = false;
                    nt.UseQuaternionSynchronization = false;
                    nt.UseQuaternionCompression = false;
                }

                _rb.isKinematic = false;
                _rb.useGravity = true;
                // Actively unpause & ensure input map is enabled when phase is set.
                SetInputPaused(false);

                UpdateHealthUI(_health.Value);
                _statsLastPersisted = default;
                _statsPendingForCloud = _combatStats.Value;
                _statsFlushImmediate = false;
            }
            else
            {
                _rb.isKinematic = true;
                _rb.useGravity = false;
            }

            if (useLegacyStateReplication)
            {
                _netPosition.OnValueChanged += OnPositionChanged;
                _netVelocity.OnValueChanged += OnVelocityChanged;
                _netIsDashing.OnValueChanged += OnDashingChanged;
            }

            // Always subscribe to yaw changes for new sync system
            _netYaw.OnValueChanged += OnYawChanged;

            // Phase resync safety net for owner: flip quickly once we're in a Match_* scene.
            if (IsOwner)
                StartCoroutine(CoPhaseReconcileKickoff());
        }
        // Ensures local phase doesn't linger as Lobby after entering Match_*.

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();

            _rb.useGravity = true;
            _rb.interpolation = RigidbodyInterpolation.None;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Prevent physics-induced tipping while allowing scripted yaw.
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            var root = modelRoot ? modelRoot : transform;
            _renderers = root.GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);

            _stamina = sprintStaminaMax;
        }

        // Duplicate OnNetworkSpawn removed; the primary OnNetworkSpawn already exists and subscribes to OnYawChanged.
        // (The duplicate block caused methods to nest and broke modifiers.)

        // Duplicate OnNetworkDespawn removed; the primary one later in the file handles cleanup and OnYawChanged.

        // Removed unused alternate yaw handler; we already use OnYawChanged(float _, float newVal).

        // Hook up replication so remote players and server receive yaw updates.

        // ==== HUD binding API (for PlayerHUDBinder) ====
        public void AssignHud(Image sprintFillUI, TMP_Text sprintLabelUI, Image dashFillUI, TMP_Text dashLabelUI, Image healthFillUI = null, TMP_Text healthLabelUI = null)
        {
            // Only bind HUD for the local owner; remote ghosts should never drive this UI.
            if (!IsOwner)
                return;

            if (sprintFillUI) sprintFill = sprintFillUI;
            if (sprintLabelUI) sprintLabel = sprintLabelUI;
            if (dashFillUI) dashFill = dashFillUI;
            if (dashLabelUI) dashLabel = dashLabelUI;
            if (healthFillUI) healthFill = healthFillUI;
            if (healthLabelUI) healthLabel = healthLabelUI;

            // Immediately sync the bar/label to the replicated health on bind.
            UpdateHealthUI(_health.Value);
        }


// Brief dev comment: guard HUD binding to the local owner and force an initial sync from the NetworkVariable.

        public void AssignHud(Component root)
        {
            // Defensive: only the local owner should ever have a HUD wired to this PlayerNetwork.
            if (!IsOwner || !root)
                return;

            sprintFill ??= root.GetComponentInChildren<Image>(true);
            sprintLabel ??= root.GetComponentInChildren<TMP_Text>(true);

            foreach (var img in root.GetComponentsInChildren<Image>(true))
            {
                if (!img) continue;
                var name = img.gameObject.name;
                if (dashFill == null && name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0) { dashFill = img; continue; }
                if (healthFill == null && name.IndexOf("health", StringComparison.OrdinalIgnoreCase) >= 0) { healthFill = img; }
            }

            foreach (var txt in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!txt) continue;
                var name = txt.gameObject.name;
                if (dashLabel == null && name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0) { dashLabel = txt; continue; }
                if (healthLabel == null && name.IndexOf("health", StringComparison.OrdinalIgnoreCase) >= 0) { healthLabel = txt; }
            }

            // When a new HUD root is discovered, snap the visuals to the replicated health.
            UpdateHealthUI(_health.Value);
        }

// Brief dev comment: mirror the image-based AssignHud guard and keep initial sync driven from the NetworkVariable.

        public void ClearHud()
        {
            sprintFill = null; sprintLabel = null; dashFill = null; dashLabel = null; healthFill = null; healthLabel = null;
        }

        void OnHealthChanged(float previous, float current)
        {
            // Health NetworkVariable changed (server → clients); drive the HUD from this hook.
            // We still pass the event value in, but UpdateHealthUI pulls from _health to stay authoritative.
            UpdateHealthUI(current);
        }

// Brief dev comment: make explicit that the NetworkVariable change is the single source of health UI updates.

        void UpdateHealthUI(float current)
        {
            // Only the local owner ever drives this HUD; remotes/server have no bar to update.
            if (!IsOwner)
                return;

            // Always trust the replicated NetworkVariable value so the bar/label stay in lockstep with the server.
            current = Mathf.Clamp(_health.Value, 0f, 100f);

            if (healthFill)
            {
                // 0–100 → 0–1 fill, with a hard snap to empty when dead.
                healthFill.fillAmount = current <= 0f ? 0f : current / 100f;
            }

            if (healthLabel)
            {
                // Show a clamped integer HP value in 3-digit format (000–999).
                int value = Mathf.RoundToInt(current);
                value = Mathf.Clamp(value, 0, 999);
                healthLabel.text = value.ToString("D3");
            }
        }

// Brief dev comment: gate updates to the owning client and always render from the replicated _health value for consistent bar/label updates.

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();

            _netLoadout.OnValueChanged -= OnNetLoadoutChanged;
            _health.OnValueChanged      -= OnHealthChanged;
            _combatStats.OnValueChanged -= OnCombatStatsChanged;
            _netYaw.OnValueChanged      -= OnYawChanged;

            if (useLegacyStateReplication)
            {
                _netPosition.OnValueChanged  -= OnPositionChanged;
                _netVelocity.OnValueChanged  -= OnVelocityChanged;
                _netIsDashing.OnValueChanged -= OnDashingChanged;
            }

            CleanupInputActions();
            enabled = false;
        }

// Brief dev comment: unsubscribe health/combat listeners on despawn so we don’t accumulate extra UI callbacks across respawns.

        public override void OnDestroy()
        {
            base.OnDestroy();
            CleanupInputActions();
        }

        void OnCombatStatsChanged(CombatStats previous, CombatStats current)
// Brief dev comment.
        {
            if (!IsOwner) return;
            _statsPendingForCloud = current;
            QueueStatsFlush();
        }

        void QueueStatsFlush(bool immediate = false)
        {
            if (!IsOwner) return;
            if (immediate) _statsFlushImmediate = true;
            if (_statsSaveRoutine == null)
                _statsSaveRoutine = StartCoroutine(CoFlushStatsToCloud());
        }

        IEnumerator CoFlushStatsToCloud()
        {
            while (true)
            {
                var waitSeconds = _statsFlushImmediate ? 0f : 1.5f;
                _statsFlushImmediate = false;
                if (waitSeconds > 0f)
                {
                    float elapsed = 0f;
                    while (elapsed < waitSeconds)
                    {
                        if (_statsFlushImmediate)
                            break;
                        elapsed += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    if (_statsFlushImmediate)
                        continue;
                }

                var pending = _statsPendingForCloud;
                int deltaKills = pending.kills - _statsLastPersisted.kills;
                int deltaDeaths = pending.deaths - _statsLastPersisted.deaths;
                int deltaDamage = pending.damage - _statsLastPersisted.damage;

                if (deltaKills <= 0 && deltaDeaths <= 0 && deltaDamage <= 0)
                    break;

                var task = CloudSaveClient.Instance.AppendStatsAsync(deltaKills, deltaDeaths, deltaDamage);
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted || task.IsCanceled)
                {
                    var message = task.Exception?.GetBaseException()?.Message ?? "Unknown";
                    Debug.LogWarning($"[CloudSave] Append stats failed: {message}. Retrying...");
                    for (float retry = 0f; retry < 5f; retry += Time.unscaledDeltaTime)
                        yield return null;
                    _statsFlushImmediate = true;
                    continue;
                }

                if (!task.Result)
                {
                    Debug.LogWarning("[CloudSave] Append stats returned false. Retrying...");
                    for (float retry = 0f; retry < 5f; retry += Time.unscaledDeltaTime)
                        yield return null;
                    _statsFlushImmediate = true;
                    continue;
                }

                _statsLastPersisted = pending;
            }

            _statsSaveRoutine = null;
        }

        internal void RegisterScoreboard(MatchScoreboardPanel panel)
        {
            if (panel == null) return;
            _scoreboard = panel;
            _scoreboardBindTries = 0;
        }

        internal void UnregisterScoreboard(MatchScoreboardPanel panel)
        {
            if (_scoreboard == panel) _scoreboard = null;
        }

        public static ClientRpcParams TargetClientParams(ulong clientId)
        {
            return new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[1] { clientId }
                }
            };
        }

        static FixedString64Bytes ToFixedString64(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return default;
            if (value.Length > 62) value = value.Substring(0, 62);
            return value;
        }

        static FixedString128Bytes ToFixedString128(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return default;
            if (value.Length > 126) value = value.Substring(0, 126);
            return value;
        }

        internal static void InstallDeathRecapCallbacks(System.Action<DeathRecapPayload> onShow, System.Action onHide)
        {
            s_onShowDeathRecap = onShow;
            s_onHideDeathRecap = onHide;
        }

        internal static void RemoveDeathRecapCallbacks(System.Action<DeathRecapPayload> onShow, System.Action onHide)
        {
            if (s_onShowDeathRecap == onShow) s_onShowDeathRecap = null;
            if (s_onHideDeathRecap == onHide) s_onHideDeathRecap = null;
        }

        internal static void InvokeHideDeathRecap() => s_onHideDeathRecap?.Invoke();

        public string GetDisplayName()
        {
            if (!_playerName.Value.IsEmpty)
            {
                var name = _playerName.Value.ToString();
                CacheIdentityFor(OwnerClientId, name, null);
                return name;
            }

            if (IsOwner)
            {
                var local = Game.Services.PlayerIdentityState.LocalDisplayName;
                if (!string.IsNullOrWhiteSpace(local))
                {
                    CacheIdentityFor(OwnerClientId, local, null);
                    return local;
                }
            }

            if (s_lastKnownNames.TryGetValue(OwnerClientId, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;

            return $"Player {OwnerClientId}";
        }

        public string GetIconId()
        {
            if (!_playerIconId.Value.IsEmpty)
            {
                var icon = _playerIconId.Value.ToString();
                CacheIdentityFor(OwnerClientId, null, icon);
                return icon;
            }

            if (IsOwner)
            {
                var local = Game.Services.PlayerIdentityState.LocalIconId;
                if (!string.IsNullOrWhiteSpace(local))
                {
                    CacheIdentityFor(OwnerClientId, null, local);
                    return local;
                }
            }

            if (s_lastKnownIconIds.TryGetValue(OwnerClientId, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;

            return null;
        }

        /// <summary>
        /// Lookup last-known icon for any client id. Falls back to a live PlayerNetwork if present.
        /// </summary>
        public static string GetCachedIconId(ulong clientId)
        {
            if (s_lastKnownIconIds.TryGetValue(clientId, out var icon) && !string.IsNullOrWhiteSpace(icon))
                return icon;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.ConnectedClients.TryGetValue(clientId, out var cc))
            {
                var pn = cc.PlayerObject ? cc.PlayerObject.GetComponent<PlayerNetwork>() : null;
                if (pn != null) return pn.GetIconId();
            }
            return null;
        }
        // Exposes the existing identity cache so UI can show icons even when the player object is culled.

        internal void NotifyKilledServer(PlayerNetwork killer, float damageAmount)
        {
            if (!IsServer) return;
            var payload = BuildDeathRecapPayload(killer, damageAmount);
            SendDeathRecap(payload);
        }

        internal void ClearDeathRecapForOwner()
        {
            if (!IsServer) return;
            ClearDeathRecapClientRpc(TargetClientParams(OwnerClientId));
        }

        DeathRecapPayload BuildDeathRecapPayload(PlayerNetwork killer, float damageAmount)
        {
            var payload = new DeathRecapPayload
            {
                killerClientId = killer ? killer.OwnerClientId : ulong.MaxValue,
                damage = Mathf.Max(0f, damageAmount),
                primary = killer ? killer._netLoadout.Value.primary : (byte)PrimaryType.None,
                secondary = killer ? killer._netLoadout.Value.secondary : (byte)SecondaryType.None,
                utility = killer ? killer._netLoadout.Value.util : (byte)UtilityType.None,
                killerName = killer ? ToFixedString64(killer.GetDisplayName()) : default,
                killerIconId = killer != null ? ToFixedString128(killer.GetIconId()) : default
            };
            return payload;
        }

        void SendDeathRecap(DeathRecapPayload payload)
        {
            if (!IsServer) return;
            ShowDeathRecapClientRpc(payload, TargetClientParams(OwnerClientId));
        }

        [ClientRpc]
        void ShowDeathRecapClientRpc(DeathRecapPayload payload, ClientRpcParams rpcParams = default)
        {
            if (!IsOwner) return;
            s_onShowDeathRecap?.Invoke(payload);
        }

        [ClientRpc]
        void ClearDeathRecapClientRpc(ClientRpcParams rpcParams = default)
        {
            if (!IsOwner) return;
            s_onHideDeathRecap?.Invoke();
        }
        
        #region Loading Phase RPCs

        /// <summary>Targeted to the owning client when the round’s Loading phase begins.</summary>
        [ClientRpc]
        public void BeginLoadingPhaseClientRpc(ClientRpcParams rpcParams = default)
        {
            if (!IsOwner) return;

            // Ensure LOS/FOV visuals are active locally in Match.
            SetFovAndLosEnabled(true);

            // Immediately report LOS ready (client-side visuals enabled).
            ReportLoadingReadyToServerRpc((byte)LoadingReady.Los);

            // Kick off Cloud Save fetch → handshake → then report Loadout ready.
            StartCoroutine(CoEnsureLoadoutThenAck());
        }

        /// <summary>Owner → Server: report completion of a loading step (LOS/Loadout).</summary>
        [ServerRpc]
        public void ReportLoadingReadyToServerRpc(byte bits, ServerRpcParams serverRpcParams = default)
        {
            if (!IsServer) return;

#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var ctrl = FindFirstObjectByType<Game.Net.Match1v1Controller>(FindObjectsInactive.Exclude);
#else
            var ctrl = FindObjectOfType<Game.Net.Match1v1Controller>(false);
#endif
            ctrl?.OnClientLoadingBits(OwnerClientId, bits);
        }

        /// <summary>
        /// Owner-only: Ensure we have a Cloud Save loadout, send it to server via the existing handshake,
        /// then tell the server our loadout step is ready.
        /// </summary>
        System.Collections.IEnumerator CoEnsureLoadoutThenAck()
        {
            if (!IsOwner) yield break;

            // Try session cache first
            PlayerLoadout lo = PlayerLoadout.Default;
            bool hasCached = false;
            try { hasCached = SessionContext.TryGetLoadout(out lo); } catch { lo = PlayerLoadout.Default; }

            if (!hasCached)
            {
                var csc = CloudSaveClient.Instance;
                if (csc != null)
                {
                    var task = csc.LoadLoadoutAsync(PlayerLoadout.Default);
                    while (!task.IsCompleted) yield return null;
                    lo = task.Result;
                }
                else
                {
                    Debug.LogWarning("[PlayerNetwork] CloudSaveClient.Instance missing; falling back to defaults.");
                    lo = PlayerLoadout.Default;
                }
                try { SessionContext.SetLoadout(lo); } catch { /* ignore */ }
            }

            // Sanitize before sending to server
            if (lo.Primary == PrimaryType.None)    lo.Primary   = PrimaryType.AR;
            if (lo.Secondary == SecondaryType.None)lo.Secondary = SecondaryType.Pistol;
            if (lo.Utility == UtilityType.None)    lo.Utility   = UtilityType.Grenade;

            // Send to server through existing handshake channel so it applies authoritatively.
            var dto = new CloudSaveClient.PlayerConnectionLoadoutDTO
            {
                version = 1,
                primary   = (byte)lo.Primary,
                secondary = (byte)lo.Secondary,
                melee     = 1, // Knife
                utility   = (byte)lo.Utility
            };
            LoadoutHandshake.SendFromClient(dto);

            // Let server know our Loadout step is complete.
            ReportLoadingReadyToServerRpc((byte)LoadingReady.Loadout);
            yield break;
        }

        #endregion
        // ================================================
        // ===== Weapons: equip, switch, throw (server authoritative) =====

// Server-only helper used by match controller to force an equipped slot.
public void ForceActiveSlotServer(byte slot)
{
    if (!IsServer) return;
    if (slot > 3) return;
    _activeSlot.Value = slot;
    Debug.Log($"[Weapons] ForceActiveSlotServer -> {slot} cid={OwnerClientId}");
}

        // ===== Helpers / misc =====
        bool IsInMatchScene()
        {
            // Treat any scene that starts with "Match_" as match gameplay context.
            // This avoids client-side phase desync blocking weapon switches.
            var name = SceneManager.GetActiveScene().name;
            return !string.IsNullOrEmpty(name) && name.StartsWith("Match_");
        }

        bool IsInMatchSceneServer()
        {
            var name = SceneManager.GetActiveScene().name;
            return !string.IsNullOrEmpty(name) && name.StartsWith("Match_");
        }
        // Server accepts slot switches whenever running a Match_* scene (phase relax).

        // Client coroutine: for a few seconds after spawn, ask server to reconcile phase.
        System.Collections.IEnumerator CoPhaseReconcileKickoff()
        {
            // Apply current networked phase immediately once (helps late-joiners).
            SetPhase(_netPhase.Value);

            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < 5f)
            {
                if (IsInMatchScene() && _phase != PlayerPhase.Match)
                    RequestPhaseReconcileServerRpc();

                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        void RequestSwitchSlot(byte slot)
        {
            // Still respect pause hard-stop
            if (_inputPaused)
            {
                Debug.Log($"[Weapons] Slot switch ignored: paused={_inputPaused} phase={_phase} req={slot}");
                return;
            }

            // Allow switching if either (a) phase says Match, or (b) we're already in a Match_* scene.
            bool inMatchScene = IsInMatchScene();
            bool phaseOk = (_phase == PlayerPhase.Match);

            if (!phaseOk && !inMatchScene)
            {
                Debug.Log($"[Weapons] Slot switch ignored: not in match context (phase={_phase}, scene='{SceneManager.GetActiveScene().name}') req={slot}");
                return;
            }

            Debug.Log($"[Weapons] Slot switch request -> {slot} (phase={_phase}, scene='{SceneManager.GetActiveScene().name}')");
            // Extra context for this moment in time.
            DbgDumpWeaponsEnv($"RequestSwitchSlot({slot})");
            if (slot > 3) return;
            RequestSwitchSlotServerRpc(slot);
        }

        [ServerRpc(RequireOwnership = false)]
        void RequestSwitchSlotServerRpc(int slot, ServerRpcParams p = default)
        {
            if (!IsServer) return;
            var from = p.Receive.SenderClientId;

            // Allow during Match_* scenes even if server-side phase hasn't flipped yet.
            bool inMatchScene = IsInMatchSceneServer();
            bool phaseOk = (_phase == PlayerPhase.Match);
            if (!phaseOk && !inMatchScene)
            {
                Debug.Log($"[Weapons] Server denied slot switch (not in match context). cid={from} req={slot} phase={_phase} scene='{SceneManager.GetActiveScene().name}'");
                return;
            }

            if (slot < 0 || slot > 3)
            {
                Debug.Log($"[Weapons] Server denied slot switch (bad slot). cid={from} req={slot}");
                return;
            }

            // Rate limit: prevent spam.
            var now = Time.unscaledTime;
            if (now < _nextAllowedSlotSwitch)
            {
                Debug.Log($"[Weapons] Server denied slot switch (rate-limit). cid={from} req={slot}");
                return;
            }
            _nextAllowedSlotSwitch = now + k_MinSwitchInterval;

            // Validate against equipped loadout: Primary/Secondary always valid; Melee always allowed; Utility only if set
            bool allowed =
                slot == 0 ||
                slot == 1 ||
                slot == 2 || // melee fixed
                (slot == 3 && _netLoadout.Value.util != (byte)UtilityType.None);
            if (!allowed)
            {
                Debug.Log($"[Weapons] Server denied slot switch (utility none). cid={from} req={slot}");
                return;
            }

            _activeSlot.Value = (byte)slot;
            Debug.Log($"[Weapons] Active slot -> {slot} from cid={from} (phase={_phase}, scene='{SceneManager.GetActiveScene().name}')");
        }

        // Ask the server to write the authoritative phase for this player (scene-driven fallback).
        [ServerRpc(RequireOwnership = false)]
        void RequestPhaseReconcileServerRpc(ServerRpcParams p = default)
        {
            if (!IsServer) return;

            var desired = IsInMatchSceneServer() ? PlayerPhase.Match : PlayerPhase.Lobby;
            if (_netPhase.Value != desired)
            {
                _netPhase.Value = desired;
                Debug.Log($"[Phase] Reconciled phase -> {desired} for cid={OwnerClientId}");
            }
        }
        // Server computes desired phase from its active scene and writes `_netPhase`.

        void RequestThrowUtility()
        {
            if (_inputPaused) return;
            if (_phase != PlayerPhase.Match) return;    // Lobby: block utility throw (client gate)
            RequestThrowUtilityServerRpc();
        }

        [ServerRpc]
        void RequestThrowUtilityServerRpc(ServerRpcParams p = default)
        {
            if (_phase != PlayerPhase.Match) return;    // Lobby: block utility throw (server gate)
            if (_netLoadout.Value.util == (byte)UtilityType.None) return;

            if (Time.time - _lastThrowServerTime < k_MinThrowInterval) return;
            _lastThrowServerTime = Time.time;

            // For now, just log. We will implement grenade entity later.
#if UNITY_EDITOR
    Debug.Log($"[Weapons] Throw utility: {(UtilityType)_netLoadout.Value.util}");
#endif
        }

        void OnNetLoadoutChanged(Game.Net.NetLoadout previous, Game.Net.NetLoadout current)
        {
            _myLoadout = current.ToModel();

            if (IsOwner && _phase == PlayerPhase.Match)
            {
                OnActiveSlotChanged();
            }
        }

        void OnActiveSlotChanged()
        {
            // Owner requests equip. Remotes wait for server fan-out.
            if (!IsOwner) return;

            byte slot = _activeSlot.Value;
            Debug.Log($"[Weapons] Owner OnActiveSlotChanged slot={slot}");
            // Dump environment at the start of the slot transition.
            DbgDumpWeaponsEnv("OnActiveSlotChanged(start)");

            // Snapshot env for debugging slot 2/3/4 issues
            var primary   = GetComponent<WeaponPrimaryController>();
            var secondary = GetComponent<WeaponSecondaryController>();
            var melee     = GetComponent<WeaponMeleeController>();
            var utility   = GetComponent<WeaponUtilityController>();
            var sockets   = GetComponent<PlayerWeaponSockets>();

            int handChildren = (sockets && sockets.handMount) ? sockets.handMount.childCount : -1;
            string mountName = (sockets && sockets.handMount) ? sockets.handMount.name : "null";

            Debug.Log($"[Weapons] Env owner={OwnerClientId} hasPrimary={(bool)primary} hasSecondary={(bool)secondary} hasMelee={(bool)melee} hasUtility={(bool)utility} sockets={(bool)sockets} handMount={mountName} children={handChildren} loadout={{P={(PrimaryType)_netLoadout.Value.primary},S={(SecondaryType)_netLoadout.Value.secondary},U={(UtilityType)_netLoadout.Value.util}}}");

            // Hide all weapon views first
            if (primary) primary.SetVisible(false);
            if (secondary) secondary.SetVisible(false);
            if (melee) melee.SetVisible(false);

            // Hard guarantee: only ONE child under Hand Mount before equipping
            if (sockets && sockets.handMount)
            {
                for (int i = sockets.handMount.childCount - 1; i >= 0; i--)
                    Destroy(sockets.handMount.GetChild(i).gameObject);
            }

            // Now show and equip the active slot
            // [DirectNet] Central purge prevents multiple views when swapping fast / race conditions.
            if (slot == 0) // Primary
            {
                var type = (PrimaryType)_netLoadout.Value.primary;
                if (type == PrimaryType.None) { Debug.LogWarning("[Weapons] Equip primary skipped: None"); return; }
                if (primary) 
                { 
                    Debug.Log($"[Weapons] Equip primary request -> {type}"); 
                    primary.Equip(type, null);
                    primary.SetVisible(true);
                    // Owner gets instant local view; server RPC still follows for authority.
                    primary.RebuildLocalViewImmediate();
                } 
                else { Debug.LogWarning("[Weapons] No WeaponPrimaryController"); }
            }
            else if (slot == 1) // Secondary
            {
                var type = (SecondaryType)_netLoadout.Value.secondary;
                if (type == SecondaryType.None) { Debug.LogWarning("[Weapons] Equip secondary skipped: None"); return; }
                if (secondary) 
                { 
                    Debug.Log($"[Weapons] Equip secondary request -> {type}"); 
                    secondary.Equip(type, null);
                    secondary.SetVisible(true);
                    // Show it now for the owner; server will still validate/state-sync.
                    secondary.RebuildLocalViewImmediate();
                } 
                else { Debug.LogWarning("[Weapons] No WeaponSecondaryController"); }
            }
            // Rebuild immediately on the owner so the hand mount never looks empty.
            else if (slot == 2) // Melee
            {
                if (melee) 
                { 
                    Debug.Log("[Weapons] Equip melee (Knife)"); 
                    melee.Equip();
                    melee.SetVisible(true);
                    // Prevents a visible gap when swapping quickly to melee.
                    melee.RebuildLocalViewImmediate();
                } 
                else { Debug.LogWarning("[Weapons] No WeaponMeleeController"); }
            }
            else if (slot == 3) // Utility
            {
                if (_netLoadout.Value.util == (byte)UtilityType.None) { Debug.LogWarning("[Weapons] Equip utility skipped: None"); return; }
                var wu = GetComponent<WeaponUtilityController>();
                if (wu) 
                { 
                    Debug.Log($"[Weapons] Equip utility request -> {(UtilityType)_netLoadout.Value.util}"); 
                    wu.Equip((UtilityType)_netLoadout.Value.util);
                    // Utility shows up on the owner immediately as well.
                    wu.RebuildLocalViewImmediate();
                } 
                else { Debug.LogWarning("[Weapons] No WeaponUtilityController"); }
            }
            // After we've executed the branch, dump again to see effect.
            DbgDumpWeaponsEnv("OnActiveSlotChanged(after)");

            // Publish an atomic snapshot for clients (server only).
            if (IsServer)
            {
                var sync = GetComponent<Game.Net.Weapons.LoadoutSnapshotSync>();
                if (sync)
                {
                    var snap = sync.State.Value;
                    // Fill from your current controllers/state (sample reads shown – map to your fields):
                    snap.activeSlot = (byte)_activeSlot.Value; // 0..3
                    // snap.primaryId/secondaryId/... = your current weapon ids
                    // snap.primaryName/.../ammo/reserve/reload = from your weapon controllers

                    sync.ServerPublish(snap, incrementVersion: true);
                }
            }
        }
        // Prevents client from issuing equip calls that clear views with "None" just as the round starts.
        // Related context: owner-side local equip is triggered by _activeSlot.OnValueChanged【turn41file1†PlayerNetwork.cs†L57-L99】.

        void SetupInputAndCamera()
        {
            CleanupInputActions();

            _map = new InputActionMap("Player");

            _aMove = _map.AddAction(name: "Move", type: InputActionType.Value, expectedControlLayout: "Vector2");
            _aMove.AddCompositeBinding("2DVector")
                  .With("Up", "<Keyboard>/w")
                  .With("Down", "<Keyboard>/s")
                  .With("Left", "<Keyboard>/a")
                  .With("Right", "<Keyboard>/d");

            _aMouse = _map.AddAction(name: "MousePos", type: InputActionType.Value, binding: "<Pointer>/position");
            _aFire = _map.AddAction(name: "Fire", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            _aReload = _map.AddAction(name: "Reload", type: InputActionType.Button, binding: "<Keyboard>/r");

            _aFire.performed += OnFirePerformed;
            _aFire.canceled += OnFireCanceled;
            _aReload.performed += OnReloadPerformed;
            _aSprint = _map.AddAction(name: "Sprint", type: InputActionType.Button, binding: "<Keyboard>/leftShift");
            _aDash = _map.AddAction(name: "Dash", type: InputActionType.Button, binding: "<Keyboard>/space");

            _aSprint.performed += OnSprintPerformed;
            _aSprint.canceled += OnSprintCanceled;

            // Weapon inputs
            _aSlot1 = _map.AddAction(name: "Slot1", type: InputActionType.Button, binding: "<Keyboard>/1");
            _aSlot1.AddBinding("<Keyboard>/numpad1"); // support numpad
            _aSlot2 = _map.AddAction(name: "Slot2", type: InputActionType.Button, binding: "<Keyboard>/2");
            _aSlot2.AddBinding("<Keyboard>/numpad2");
            _aSlot3 = _map.AddAction(name: "Slot3", type: InputActionType.Button, binding: "<Keyboard>/3");
            _aSlot3.AddBinding("<Keyboard>/numpad3");
            _aSlot4 = _map.AddAction(name: "Slot4", type: InputActionType.Button, binding: "<Keyboard>/4");
            _aSlot4.AddBinding("<Keyboard>/numpad4");
            _aThrow = _map.AddAction(name: "Throw", type: InputActionType.Button, binding: "<Keyboard>/g");
            // Brief dev comment: users often hit numpad 1–4; bind both.
            _aScoreboard = _map.AddAction(name: "Scoreboard", type: InputActionType.Button, binding: "<Keyboard>/tab");

            _aDash.performed += OnDashPerformed;
            // forward dash state to weapon controller via OnDashingChanged callback already patched.
            _aSlot1.performed += OnSlot1Performed;
            _aSlot2.performed += OnSlot2Performed;
            _aSlot3.performed += OnSlot3Performed;
            _aSlot4.performed += OnSlot4Performed;
            _aThrow.performed += OnThrowPerformed;
            _aScoreboard.performed += OnScoreboardPerformed;
            _aScoreboard.canceled += OnScoreboardCanceled;

            _map.Enable();
            TryBindCamera();
            TryBindScoreboard();
        }

        void OnFirePerformed(InputAction.CallbackContext ctx) => OnFireInput(true);
        void OnFireCanceled(InputAction.CallbackContext ctx) => OnFireInput(false);
        void OnReloadPerformed(InputAction.CallbackContext ctx) => OnReloadInput();
        void OnSprintPerformed(InputAction.CallbackContext ctx) => SetSprint(true);
        void OnSprintCanceled(InputAction.CallbackContext ctx) => SetSprint(false);
        void OnSlot1Performed(InputAction.CallbackContext ctx) => RequestSwitchSlot(0);
        void OnSlot2Performed(InputAction.CallbackContext ctx) => RequestSwitchSlot(1);
        void OnSlot3Performed(InputAction.CallbackContext ctx) => RequestSwitchSlot(2);
        void OnSlot4Performed(InputAction.CallbackContext ctx) => RequestSwitchSlot(3);
        void OnThrowPerformed(InputAction.CallbackContext ctx) => RequestThrowUtility();

        void CleanupInputActions()
        {
            if (_aFire != null)
            {
                _aFire.performed -= OnFirePerformed;
                _aFire.canceled -= OnFireCanceled;
            }

            if (_aReload != null)
                _aReload.performed -= OnReloadPerformed;

            if (_aSprint != null)
            {
                _aSprint.performed -= OnSprintPerformed;
                _aSprint.canceled -= OnSprintCanceled;
            }

            if (_aDash != null)
                _aDash.performed -= OnDashPerformed;

            if (_aSlot1 != null) _aSlot1.performed -= OnSlot1Performed;
            if (_aSlot2 != null) _aSlot2.performed -= OnSlot2Performed;
            if (_aSlot3 != null) _aSlot3.performed -= OnSlot3Performed;
            if (_aSlot4 != null) _aSlot4.performed -= OnSlot4Performed;
            if (_aThrow != null) _aThrow.performed -= OnThrowPerformed;

            if (_aScoreboard != null)
            {
                _aScoreboard.performed -= OnScoreboardPerformed;
                _aScoreboard.canceled -= OnScoreboardCanceled;
            }

            _map?.Disable();
            _map?.Dispose();

            if (IsOwner)
                ShowScoreboard(false);

            _aMove = null;
            _aMouse = null;
            _aSprint = null;
            _aDash = null;
            _aFire = null;
            _aReload = null;
            _aScoreboard = null;
            _aSlot1 = _aSlot2 = _aSlot3 = _aSlot4 = _aThrow = null;
            _map = null;
        }

        void OnDashPerformed(InputAction.CallbackContext ctx)
        {
            if (_inputPaused) return;
            _dashQueuedUntil = Time.time + dashInputBuffer;
        }

// Owner-side input gate: allowed only when we're in Match phase.
void OnFireInput(bool firing)
{
    if (!IsOwner) return;
    if (_inputPaused) return;
    if (_phase != PlayerPhase.Match) return;

    var slot = _activeSlot.Value;
    if (slot == 0)      GetComponent<WeaponPrimaryController>()?.FireHeld(firing);
    else if (slot == 1) GetComponent<WeaponSecondaryController>()?.FireHeld(firing);
    else if (slot == 2) { if (firing) GetComponent<WeaponMeleeController>()?.RequestSwing(); }
}
// [DirectNet] Phase-driven gate fixes the “lobby lockout” that was blocking fire in Match.

// Same phase-driven gate for reload as fire.
void OnReloadInput()
{
    if (!IsOwner) return;
    if (_inputPaused) return;
    if (_phase != PlayerPhase.Match) return;

    var slot = _activeSlot.Value;
    if (slot == 0)      GetComponent<WeaponPrimaryController>()?.RequestReload();
    else if (slot == 1) GetComponent<WeaponSecondaryController>()?.RequestReload();
}

        public void SetInputPaused(bool paused)
        {
            _inputPaused = paused;
            _isDashing = false;
            _dashQueuedUntil = 0f;
            _inMove = Vector2.zero;

            if (_rb && !_rb.isKinematic)
            {
                var v = _rb.linearVelocity; v.x = 0f; v.z = 0f; _rb.linearVelocity = v;
            }

            if (paused)
            {
                _map?.Disable();
                ShowScoreboard(false);
            }
            else
            {
                // Guard: after phase changes or reconnects the map may be disabled – force enable.
                if (_map != null && !_map.enabled) _map.Enable();
            }

            Debug.Log($"[Input] SetInputPaused -> {paused} mapEnabled={(_map != null && _map.enabled)} phase={_phase}");
        }

        // Local-only helper so stuns can briefly pause input on the affected client.
        Coroutine _inputPauseCo;
        public void StartLocalInputPause(float seconds)
        {
            if (!IsOwner) return; // only the local owner should drive local input pause
            if (_inputPauseCo != null) StopCoroutine(_inputPauseCo);
            _inputPauseCo = StartCoroutine(CoLocalInputPause(seconds));
        }

        System.Collections.IEnumerator CoLocalInputPause(float seconds)
        {
            SetInputPaused(true);
            float end = Time.unscaledTime + Mathf.Max(0f, seconds);
            while (Time.unscaledTime < end) yield return null;
            SetInputPaused(false);
            _inputPauseCo = null;
        }
        // Brief dev comment: gives stun projectiles a safe client-side way to pause/unpause input.

        void TryBindCamera()
        {
            _cam = Camera.main;
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (_cam == null) _cam = FindFirstObjectByType<Camera>();
#else
            if (_cam == null) _cam = FindObjectOfType<Camera>();
#endif
            if (_cam != null)
            {
                _isoCam = _cam.GetComponent<IsometricCamera>() ?? _cam.gameObject.AddComponent<IsometricCamera>();
                _isoCam.follow = transform;
                _isoCam.enabled = true;

                // Only install LOS transparency + fog-of-war during MATCH
                if (_phase == PlayerPhase.Match)
                {
                    var los = _cam.GetComponent<LineOfSightTransparency>() ?? _cam.gameObject.AddComponent<LineOfSightTransparency>();
                    los.target = transform;

                    // Prevent occlusion culling issues with our transparent fades
                    _cam.useOcclusionCulling = false;

                    // Ensure the fog overlay plane exists now that we have a camera
                    FogOfWarOverlayPlane.InstallFor(_cam);
                }
                else
                {
                    // In Lobby, keep the camera clean
                    RemoveLosFromCamera();
                }
            }
        }

        void TryBindScoreboard()
        {
            if (!IsOwner || _scoreboard != null) return;
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var panel = FindFirstObjectByType<MatchScoreboardPanel>(FindObjectsInactive.Include);
#else
            var panel = FindObjectOfType<MatchScoreboardPanel>();
#endif
            if (panel != null)
            {
                panel.SetOwner(this);
            }
        }

        void ShowScoreboard(bool show)
        {
            if (!IsOwner) return;
            if (_scoreboard == null)
            {
                if (show) TryBindScoreboard();
                if (_scoreboard == null) return;
            }
            if (show && _inputPaused) show = false;
            _scoreboard.SetVisible(show);
        }

        void OnScoreboardPerformed(InputAction.CallbackContext ctx)
        {
            ShowScoreboard(true);
        }

        void OnScoreboardCanceled(InputAction.CallbackContext ctx)
        {
            ShowScoreboard(false);
        }

        void BeginSubmitNameRoutine()
        {
            if (!IsOwner || _identitySubmitted) return;
            if (_identitySubmitCo != null) StopCoroutine(_identitySubmitCo);
            _identitySubmitCo = StartCoroutine(CoSubmitPlayerIdentity());
        }

        IEnumerator CoSubmitPlayerIdentity()
        {
            const float timeoutSeconds = 6f;
            float deadline = Time.unscaledTime + timeoutSeconds;

            var ensureTask = Game.Services.PlayerIdentityState.EnsureIdentityAsync();
            while (!ensureTask.IsCompleted && Time.unscaledTime < deadline)
                yield return null;

            while (IsOwner && !_identitySubmitted)
            {
                string alias = ResolvePreferredPlayerName();
                string iconId = ResolvePreferredIconId();
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    SubmitPlayerIdentityServerRpc(alias, iconId ?? string.Empty);
                    _identitySubmitted = true;
                    yield break;
                }

                if (Time.unscaledTime >= deadline) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (IsOwner && !_identitySubmitted)
            {
                string fallbackName = $"Player {OwnerClientId}";
                string fallbackIcon = ResolvePreferredIconId();
                SubmitPlayerIdentityServerRpc(fallbackName, fallbackIcon ?? string.Empty);
                _identitySubmitted = true;
            }
        }

        static string ResolvePreferredPlayerName()
        {
            var cached = Game.Services.PlayerIdentityState.LocalDisplayName;
            if (!string.IsNullOrWhiteSpace(cached)) return cached;

            try
            {
                var auth = AuthenticationService.Instance;
                var lobby = SessionContext.CurrentLobby;
                var playerId = auth != null ? auth.PlayerId : null;

                if (lobby != null && !string.IsNullOrEmpty(playerId))
                {
                    var players = lobby.Players;
                    if (players != null)
                    {
                        for (int i = 0; i < players.Count; i++)
                        {
                            var member = players[i];
                            if (member == null || member.Id != playerId) continue;
                            if (member.Data != null)
                            {
                                if (member.Data.TryGetValue("displayName", out var display) && display != null && !string.IsNullOrWhiteSpace(display.Value))
                                    return display.Value;
                                if (member.Data.TryGetValue("username", out var username) && username != null && !string.IsNullOrWhiteSpace(username.Value))
                                    return username.Value;
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerNetwork] ResolvePreferredPlayerName failed: {ex.Message}");
            }

            return Game.Services.PlayerIdentityState.LocalDisplayName;
        }

        static string ResolvePreferredIconId()
        {
            if (!string.IsNullOrWhiteSpace(Game.Services.PlayerIdentityState.LocalIconId))
                return Game.Services.PlayerIdentityState.LocalIconId;

            try
            {
                var auth = AuthenticationService.Instance;
                var lobby = SessionContext.CurrentLobby;
                var playerId = auth != null ? auth.PlayerId : null;

                if (lobby != null && !string.IsNullOrEmpty(playerId))
                {
                    var players = lobby.Players;
                    if (players != null)
                    {
                        for (int i = 0; i < players.Count; i++)
                        {
                            var member = players[i];
                            if (member == null || member.Id != playerId) continue;
                            if (member.Data != null && member.Data.TryGetValue("profileIcon", out var icon) && icon != null && !string.IsNullOrWhiteSpace(icon.Value))
                                return icon.Value;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerNetwork] ResolvePreferredIconId failed: {ex.Message}");
            }

            return Game.Services.PlayerIdentityState.LocalIconId;
        }

        [ServerRpc(RequireOwnership = false)]
        void SubmitPlayerIdentityServerRpc(FixedString64Bytes alias, FixedString128Bytes iconId, ServerRpcParams rpcParams = default)
        {
            var sanitizedName = SanitizeAlias(alias);
            if (sanitizedName.IsEmpty) return;

            var sanitizedIcon = SanitizeIconId(iconId);
            _playerName.Value = sanitizedName;
            _playerIconId.Value = sanitizedIcon;
        }

        static FixedString64Bytes SanitizeAlias(FixedString64Bytes alias)
        {
            var raw = alias.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return default;

            string cleaned = raw.Trim();
            if (cleaned.Length > MaxDisplayNameLength)
                cleaned = cleaned.Substring(0, MaxDisplayNameLength);
            cleaned = cleaned.Replace('\n', ' ').Replace('\r', ' ');

            Span<char> buffer = stackalloc char[cleaned.Length];
            int len = 0;
            for (int i = 0; i < cleaned.Length; i++)
            {
                char c = cleaned[i];
                if (char.IsControl(c)) continue;
                buffer[len++] = c;
            }

            cleaned = new string(buffer.Slice(0, len));
            if (string.IsNullOrWhiteSpace(cleaned)) return default;
            FixedString64Bytes result = cleaned;
            return result;
        }

        static FixedString128Bytes SanitizeIconId(FixedString128Bytes iconId)
        {
            var raw = iconId.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return default;

            string cleaned = raw.Trim();
            if (cleaned.Length > MaxIconIdLength)
                cleaned = cleaned.Substring(0, MaxIconIdLength);

            Span<char> buffer = stackalloc char[cleaned.Length];
            int len = 0;
            for (int i = 0; i < cleaned.Length; i++)
            {
                char c = cleaned[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    buffer[len++] = c;
            }

            if (len == 0) return default;
            return new FixedString128Bytes(new string(buffer.Slice(0, len)));
        }

        void LateUpdate()
        {
            if (!IsOwner) return;
            if (_isoCam == null && _camBindTries < 60) { _camBindTries++; TryBindCamera(); }
            if (_scoreboard == null && _scoreboardBindTries < 120) { _scoreboardBindTries++; TryBindScoreboard(); }
            if (_inputPaused) { UpdateUI(); return; }
        }

        bool CanWriteYaw()
        {
            if (!networkSyncYaw) return false;
            if (!IsSpawned) return false;
            if (!IsOwner) return false;

            var nm = NetworkManager.Singleton;
            if (nm == null) return false;
            if (!nm.IsClient || !nm.IsConnectedClient) return false;

            return nm.LocalClientId == OwnerClientId;
        }

        void Update()
        {
            if (!IsOwner) { InterpolateRemotePlayer(); return; }
            if (_inputPaused) { UpdateUI(); return; }

            _inMove = _aMove?.ReadValue<Vector2>() ?? Vector2.zero;
            _inMouse = _aMouse?.ReadValue<Vector2>() ?? Vector2.zero;
            // Read inputs
            _inSprint = _aSprint != null && _aSprint.IsPressed();
            // Reset per-frame so fallback (ray/plane) can solve yaw when inside the deadzone
            _hasValidYaw = false;

            // Calculate target yaw from mouse position every frame for responsiveness
            if (_cam)
            {
                var ray = _cam.ScreenPointToRay(_inMouse);

                // Owner computes yaw from screen-space mouse; others return early above.
                if (aimUsingScreenSpace)
                {
                    var cam = Camera.main ? Camera.main : _cam;
                    var sp = cam.WorldToScreenPoint(transform.position);
                    // Use the Input System pointer position we already read into _inMouse
                    var delta = _inMouse - new Vector2(sp.x, sp.y);
                    // Brief dev comment: avoids mismatch with legacy Input.mousePosition.

                    if (delta.sqrMagnitude >= (screenAimMinPixels * screenAimMinPixels))
                    {
                        Vector3 camRightXZ = Vector3.ProjectOnPlane(cam.transform.right,   Vector3.up).normalized;
                        Vector3 camFwdXZ   = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;

                        // Screen X -> camera right; screen Y -> camera *forward* (not up) on ground plane
                        Vector3 dir = camRightXZ * delta.x + camFwdXZ * delta.y;
                        dir.y = 0f;
                        if (dir.sqrMagnitude > 0.0001f)
                        {
                            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                            _targetYaw = yaw;
                            _hasValidYaw = true;

                            if (CanWriteYaw())
                            {
                                float now = Time.unscaledTime;
                                if (now >= _nextYawSendTime || Mathf.Abs(Mathf.DeltaAngle(_lastSentYaw, yaw)) >= yawSendThresholdDeg)
                                {
                                    _netYaw.Value = yaw;
                                    _lastSentYaw = yaw;
                                    _nextYawSendTime = now + (1f / Mathf.Max(1f, yawSendRateHz));
                                }
                            }
                        }
                    }
                }

                // Brief dev comment: unify screen-space aim across the class to avoid mixed input paths.

                // Fallback: ray/plane aim if screen-space didn’t resolve
                if (!_hasValidYaw)
                {
                    // Switch to mathematical plane intersection for robustness: always computes direction even without ground colliders.
                    // Intersect against the player's height so uneven level offsets do not skew the aim vector.
                    bool solved = false;
                    Plane groundPlane = new Plane(Vector3.up, transform.position);
                    if (groundPlane.Raycast(ray, out float enter) && enter > 0f)
                    {
                        Vector3 hit = ray.GetPoint(enter);
                        Vector3 to = hit - transform.position;
                        to.y = 0f;
                        float sqr = to.sqrMagnitude;

                        float minWorldSqr = 0f;
                        if (_cam)
                        {
                            float orthoSize = _isoCam ? Mathf.Max(0.01f, _isoCam.orthographicSize) : 20f;
                            float pixels = Mathf.Max(1f, _cam.pixelHeight);
                            minWorldSqr = Mathf.Pow(screenAimMinPixels * orthoSize / pixels, 2f);
                        }

                        if (sqr > minWorldSqr)
                        {
                            _targetYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                            solved = true;

                            if (CanWriteYaw())
                            {
                                float now = Time.unscaledTime;
                                if (now >= _nextYawSendTime || Mathf.Abs(Mathf.DeltaAngle(_lastSentYaw, _targetYaw)) >= yawSendThresholdDeg)
                                {
                                    _netYaw.Value = _targetYaw;
                                    _lastSentYaw = _targetYaw;
                                    _nextYawSendTime = now + (1f / Mathf.Max(1f, yawSendRateHz));
                                }
                            }
                        }
                    }

                    _hasValidYaw = solved;
                }

                // Brief dev comment: Using Plane.Raycast ensures we get a hit point without relying on physics colliders/layers, fixing cases where ground isn't set up correctly. For precise aiming on uneven terrain, use Physics.Raycast with a properly configured groundMask (e.g., including "Ground" layer) and ensure all floor/terrain colliders are on that layer.
            }

            UpdateUI();
        }

        // Load CloudSave -> send to server (owner only). Validated server-side.
        System.Collections.IEnumerator CoLoadAndSendLoadout()
        {
            // This coroutine now runs in LOBBY phase to load and send the loadout for caching
            if (!IsOwner) yield break;

            // Be defensive around lobby-time singletons.
            PlayerLoadout lo = PlayerLoadout.Default;
            bool hasCached = false;
            try { hasCached = SessionContext.TryGetLoadout(out lo); } catch { lo = PlayerLoadout.Default; }

            if (!hasCached)
            {
                // CloudSaveClient may not exist in some lobby-only scenes; guard it.
                var csc = CloudSaveClient.Instance;
                if (csc == null)
                {
                    Debug.LogWarning("[PlayerNetwork] CloudSaveClient.Instance is null; using default loadout.");
                    lo = PlayerLoadout.Default;
                }
                else
                {
                    var task = csc.LoadLoadoutAsync(PlayerLoadout.Default);
                    while (!task.IsCompleted) yield return null;
                    lo = task.Result; // PlayerLoadout is a struct; '??' not applicable.
                    // If you ever want a safety fallback, do it in the async method or by a separate validity check.
                }

                try { SessionContext.SetLoadout(lo); } catch { /* optional cache; ignore if context absent */ }
            }

            // CRITICAL: Guarantee no None values before sending to server
            // This ensures players ALWAYS have a full loadout (AR/Pistol/Knife/Grenade)
            bool changed = false;
            if (lo.Primary == PrimaryType.None) { lo.Primary = PrimaryType.AR; changed = true; }
            if (lo.Secondary == SecondaryType.None) { lo.Secondary = SecondaryType.Pistol; changed = true; }
            if (lo.Utility == UtilityType.None) { lo.Utility = UtilityType.Grenade; changed = true; }

            _myLoadout = lo;
            
            // Self-healing: if we sanitized, save the corrected loadout back to Cloud Save
            if (changed)
            {
                var csc = CloudSaveClient.Instance;
                if (csc != null)
                {
                    var saveTask = csc.SaveLoadoutAsync(lo);
                    while (!saveTask.IsCompleted) yield return null;
                    if (!saveTask.Result)
                    {
                        Debug.LogWarning("[PlayerNetwork] Self-healing save failed");
                    }
                }
            }
            
            // Send via LoadoutHandshake (same path as LoadoutUI)
            var dto = new CloudSaveClient.PlayerConnectionLoadoutDTO
            {
                version = 1,
                primary = (byte)lo.Primary,
                secondary = (byte)lo.Secondary,
                melee = 1, // Knife
                utility = (byte)lo.Utility
            };
            
            Debug.Log($"[PlayerNetwork] Sending loadout via handshake: P={lo.Primary} S={lo.Secondary} M=Knife U={lo.Utility}");
            LoadoutHandshake.SendFromClient(dto);
        }
        // Runs in Lobby to load from Cloud Save and send to server for pre-join caching.

        void FixedUpdate()
        {
            if (!IsOwner) return;
            if (_rb == null) return;

            // Do not write velocity to kinematic bodies.
            if (_rb.isKinematic) return;

            if (_inputPaused)
            {
                var v0 = _rb.linearVelocity; v0.x = 0f; v0.z = 0f; _rb.linearVelocity = v0;
                _rb.angularVelocity = Vector3.zero;
                return;
            }

            float dt = Time.fixedDeltaTime;
            float now = Time.time;

            Vector3 fwd = Vector3.forward, right = Vector3.right;
            if (_cam)
            {
                fwd = _cam.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                right = _cam.transform.right; right.y = 0f; right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
            }

            // Use the yaw calculated in Update() for instant mouse-to-rotation
            // This ensures player always faces mouse cursor (weapon muzzle points at cursor)
            float yaw = _hasValidYaw ? _targetYaw : transform.eulerAngles.y;

            if (!_isDashing && now <= _dashQueuedUntil && now >= _dashReadyAt)
            {
                _dashQueuedUntil = 0f;

                _isDashing = true;
                _dashStartTime = now;
                _dashEndTime = now + dashDuration;
                _dashReadyAt = _dashEndTime + dashCooldown;

                _dashYaw = yaw;
                var fwdXZ = Quaternion.Euler(0f, _dashYaw, 0f) * Vector3.forward; fwdXZ.y = 0f;
                _dashDirXZ = fwdXZ.sqrMagnitude > 1e-4f ? fwdXZ.normalized : Vector3.forward;

                SendStateUpdateServerRpc(transform.position, _dashYaw, _rb.linearVelocity, _isDashing);
            }

            if (_isDashing)
            {
                var dashRot = Quaternion.Euler(0f, _dashYaw, 0f);
                if (_rb) _rb.MoveRotation(dashRot);
                transform.rotation = dashRot;

                float tNorm = Mathf.Clamp01((now - _dashStartTime) / dashDuration);
                float sPrime = 0.5f * Mathf.PI * Mathf.Sin(Mathf.PI * tNorm);
                float speed = (dashDistance / dashDuration) * sPrime;

                var v = _rb.linearVelocity;
                v.x = _dashDirXZ.x * speed;
                v.z = _dashDirXZ.z * speed;
                _rb.linearVelocity = v;

                if (now >= _dashEndTime)
                {
                    _isDashing = false;
                    SendStateUpdateServerRpc(transform.position, yaw, _rb.linearVelocity, _isDashing);
                }
                return;
            }

            bool canSprint = _stamina > 0.05f;
            bool wantSprint = _inSprint && canSprint && _inMove.sqrMagnitude > 0.01f;

            if (wantSprint) { _stamina = Mathf.Max(0f, _stamina - sprintDrainPerSec * dt); _sprintRegenResumeAt = now + sprintRegenDelay; }
            else if (now >= _sprintRegenResumeAt) { _stamina = Mathf.Min(sprintStaminaMax, _stamina + sprintRegenPerSec * dt); }

            Vector3 wish = (_cam ? _cam.transform.right : Vector3.right) * _inMove.x
                         + (_cam ? _cam.transform.forward : Vector3.forward) * _inMove.y;
            wish.y = 0f;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            if (_hasValidYaw)
            {
                float cur = transform.eulerAngles.y;
                float next = Mathf.LerpAngle(cur, _targetYaw, rotationLerpSpeed * dt);

                var q = Quaternion.Euler(0f, next, 0f);
                transform.rotation = q;
                if (_rb && !_rb.isKinematic) _rb.MoveRotation(q); // play nice with physics
            }
            // Rotation application remains universal; the source of _targetYaw differs by role.
            // Brief dev comment: universal application keeps visuals consistent and lets server broadcast via NetworkTransform.

            float speedMove = moveSpeed * (wantSprint ? sprintMultiplier : 1f);
            var vel = _rb.linearVelocity;
            vel.x = wish.x * speedMove;
            vel.z = wish.z * speedMove;
            _rb.linearVelocity = vel;
            _rb.angularVelocity = Vector3.zero;

            if (useLegacyStateReplication && Time.frameCount % 4 == 0) // ~15 Hz when enabled
            {
                SendStateUpdateServerRpc(transform.position, yaw, vel, _isDashing);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        void SendStateUpdateServerRpc(Vector3 position, float yaw, Vector3 velocity, bool isDashing)
        {
            _netPosition.Value = position;
            _netYaw.Value = yaw;
            _netVelocity.Value = velocity;
            _netIsDashing.Value = isDashing;
        }

        // Authoritative equip. Validates indices and applies to replicated var.
        [ServerRpc(RequireOwnership = true)]
        void EquipLoadoutServerRpc(byte primary, byte secondary, byte melee, byte util, ServerRpcParams p = default)
        {
// Validate ranges
if (primary > (byte)PrimaryType.Sniper) primary = 0;
if (secondary > (byte)SecondaryType.MachinePistol) secondary = 0;
if (util > (byte)UtilityType.Stun) util = (byte)UtilityType.Grenade;

// Server-only fallback: never allow None in any slot
if (primary == 0) primary = (byte)PrimaryType.AR;
if (secondary == 0) secondary = (byte)SecondaryType.Pistol;
if (melee == 0) melee = 1; // Knife
// Utility already clamped above; also fix None explicitly:
if (util == 0) util = (byte)UtilityType.Grenade;

_netLoadout.Value = new NetLoadout { primary = primary, secondary = secondary, melee = melee, util = util };

// Hard guarantee: sanitize + health = 100, then equip primary.
ServerEnsureValidLoadoutAndHealth();

// Log for traceability
#if UNITY_EDITOR
Debug.Log($"[PlayerNetwork] Equipped loadout (sanitized) P={(PrimaryType)_netLoadout.Value.primary} S={(SecondaryType)_netLoadout.Value.secondary} M={_netLoadout.Value.melee} U={(UtilityType)_netLoadout.Value.util} for {OwnerClientId}");
#endif
// Called from the same area where loadout RPC is processed【turn42file6†PlayerNetwork.cs†L1-L20】.

// Server RPC now hardens "no None" invariants so UI/slots always populate.
        }

// NGO: guard at runtime instead of using Mirror's [Server] attribute.
public void ApplyPreJoinLoadoutServer(byte primary, byte secondary, byte melee, byte util)
{
    if (!IsServer) return;

    // Harden against missing/None payloads from pre-join path.
    if (primary == 0)   primary   = (byte)PrimaryType.AR;
    if (secondary == 0) secondary = (byte)SecondaryType.Pistol;
    if (util == 0 || util > (byte)UtilityType.Stun) util = (byte)UtilityType.Grenade;

    // Melee: default to Knife if unset (assume 1 == Knife in your enum; adjust if needed).
    if (melee == 0) melee = 1;

    var loadout = _netLoadout.Value;
    loadout.primary = primary;
    loadout.secondary = secondary;
    loadout.melee = melee;
    loadout.util = util;
    _netLoadout.Value = loadout;

// Normalize and ensure full health before any auto-equip.
ServerEnsureValidLoadoutAndHealth(sanitizeOnly: false);

#if UNITY_EDITOR
Debug.Log($"[DirectNet] ApplyPreJoinLoadoutServer (sanitized) -> P{_netLoadout.Value.primary}/S{_netLoadout.Value.secondary}/M{_netLoadout.Value.melee}/U{_netLoadout.Value.util}");
#endif
// This runs at spawn time where the pre-join payload is applied【turn42file6†PlayerNetwork.cs†L22-L37】.    // Ensure a weapon is actually in-hand after seeding.
    ServerAutoEquipPrimary();
}

// Server consumes pre-join cache and guarantees no slot is None; also equips primary.
// Removes missing [Server]/ServerAttribute and switches to byte params to avoid type coupling.

        // Server utility you can call from spawners/match controllers.
        // Ensures loadout invariants and equips weapons so UI/slots are populated.
        public void ServerEnsureLoadoutValidAndEquip()
        {
            if (!IsServer) return;

            var lo = _netLoadout.Value;
            if (lo.primary == 0)   lo.primary   = (byte)PrimaryType.AR;
            if (lo.secondary == 0) lo.secondary = (byte)SecondaryType.Pistol;
            if (lo.util == 0 || lo.util > (byte)UtilityType.Stun) lo.util = (byte)UtilityType.Grenade;
            if (lo.melee == 0) lo.melee = 1; // assume Knife

            _netLoadout.Value = lo;

            // Equip on server (authoritative) and replicate down.
            ServerAutoEquipPrimary();

            // Pre-initialize other slots so they’re ready on first switch.
            var ws = GetComponent<Game.Net.Weapons.WeaponSecondaryController>();
            if (ws) ws.Equip((SecondaryType)lo.secondary, null);
            var wm = GetComponent<Game.Net.Weapons.WeaponMeleeController>();
            if (wm) wm.Equip();

        }

        void OnPositionChanged(Vector3 _, Vector3 newVal)
        {
            if (IsOwner) return;
            AddStateToBuffer(newVal, _netYaw.Value, _netVelocity.Value, _netIsDashing.Value);
        }        void OnYawChanged(float _, float newVal)
        {
            if (IsOwner) return;

            // Handle new yaw synchronization (direct NetworkVariable)
            if (networkSyncYaw)
            {
                // Apply rotation directly for remote players
                transform.rotation = Quaternion.Euler(0f, newVal, 0f);
                if (_rb) _rb.MoveRotation(transform.rotation);
                return;
            }

            // Legacy replication fallback
            AddStateToBuffer(_netPosition.Value, newVal, _netVelocity.Value, _netIsDashing.Value);
        }

        void OnVelocityChanged(Vector3 _, Vector3 newVal)
        {
            if (IsOwner) return;
            AddStateToBuffer(_netPosition.Value, _netYaw.Value, newVal, _netIsDashing.Value);
        }

        void OnDashingChanged(bool _, bool newVal)
        {
            if (IsOwner) return;
            AddStateToBuffer(_netPosition.Value, _netYaw.Value, _netVelocity.Value, newVal);
            IsDashing = newVal;
            DashChanged?.Invoke(newVal);
            var wp = GetComponent<Game.Net.Weapons.WeaponPrimaryController>();
            if (wp) wp.OnDashChanged(newVal);
        }

        void AddStateToBuffer(Vector3 pos, float yaw, Vector3 vel, bool dash)
        {
            var state = new NetworkState
            {
                position = pos,
                yaw = yaw,
                velocity = vel,
                timestamp = NetworkManager.Singleton ? NetworkManager.Singleton.ServerTime.TimeAsFloat : Time.time,
                isDashing = dash
            };

            if (_stateCount >= _stateBuffer.Length)
            {
                for (int i = 1; i < _stateBuffer.Length; i++) _stateBuffer[i - 1] = _stateBuffer[i];
                _stateBuffer[_stateBuffer.Length - 1] = state;
            }
            else
            {
                _stateBuffer[_stateCount++] = state;
            }
        }

        void InterpolateRemotePlayer()
        {
            if (!useLegacyStateReplication) return; // NetworkTransform drives remotes
            if (_stateCount < 2) return;

            float currentTime = (NetworkManager.Singleton ? NetworkManager.Singleton.ServerTime.TimeAsFloat : Time.time) - interpolationDelay;

            NetworkState from = default, to = default;
            bool found = false;

            for (int i = 0; i < _stateCount - 1; i++)
            {
                if (_stateBuffer[i].timestamp <= currentTime && _stateBuffer[i + 1].timestamp > currentTime)
                { from = _stateBuffer[i]; to = _stateBuffer[i + 1]; found = true; break; }
            }

            if (!found)
            {
                var latest = _stateBuffer[_stateCount - 1];
                float deltaTime = currentTime - latest.timestamp;
                if (deltaTime < extrapolationLimit)
                {
                    transform.position = latest.position + latest.velocity * deltaTime;
                    transform.rotation = Quaternion.Euler(0f, latest.yaw, 0f);
                }
                return;
            }

            float t = Mathf.InverseLerp(from.timestamp, to.timestamp, currentTime);
            transform.position = Vector3.Lerp(from.position, to.position, t);
            transform.rotation = Quaternion.Slerp(
                Quaternion.Euler(0f, from.yaw, 0f),
                Quaternion.Euler(0f, to.yaw, 0f),
                t
            );
        }

        void UpdateUI()
        {
            float sprint01 = Mathf.InverseLerp(0f, sprintStaminaMax, _stamina);
            if (sprintFill) sprintFill.fillAmount = sprint01;
            if (sprintLabel) sprintLabel.text = $"Sprint {(int)(sprint01 * 100f)}%";

            float dash01;
            if (_isDashing) dash01 = 0f;
            else if (Time.time >= _dashReadyAt) dash01 = 1f;
            else dash01 = Mathf.InverseLerp(_dashEndTime, _dashReadyAt, Time.time);

            if (dashFill) dashFill.fillAmount = dash01;
            if (dashLabel) dashLabel.text = dash01 >= 1f ? "Dash Ready" : $"Dash {(int)(dash01 * 100f)}%";
        }

        void TrySnapToGroundImmediate()
        {
            float lift = groundSkin;
            if (_capsule)
            {
                float half = Mathf.Max(0f, _capsule.height * 0.5f);
                lift = Mathf.Max(lift, half - _capsule.radius + 0.01f);
            }

            var start = transform.position + Vector3.up * groundSnapUp;
            if (Physics.Raycast(start, Vector3.down, out var hit, groundSnapUp + groundSnapDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                transform.position = hit.point + Vector3.up * lift;
            }
        }

        void ResolveInitialPenetration()
        {
            var c = _capsule ? _capsule : GetComponent<CapsuleCollider>();
            if (!c) return;

            int iters = 0;
            const int maxIters = 5;

            while (iters++ < maxIters)
            {
                GetCapsuleWorld(c, transform, out var p0, out var p1, out var r);
                var overlaps = Physics.OverlapCapsule(p0, p1, r, groundMask, QueryTriggerInteraction.Ignore);
                if (overlaps == null || overlaps.Length == 0) break;

                Vector3 push = Vector3.zero;
                foreach (var col in overlaps)
                {
                    if (col == c) continue;
                    if (Physics.ComputePenetration(
                        c, transform.position, transform.rotation,
                        col, col.transform.position, col.transform.rotation,
                        out var dir, out var dist))
                    {
                        if (dist > 0f) push += dir * dist;
                    }
                }

                if (push.sqrMagnitude < 1e-6f) break;
                if (push.y < 0f) push.y = 0f;
                transform.position += push + Vector3.up * groundSkin;
            }
        }

        static void GetCapsuleWorld(CapsuleCollider cap, Transform t, out Vector3 p0, out Vector3 p1, out float radius)
        {
            Vector3 center = t.TransformPoint(cap.center);
            float height = Mathf.Max(cap.height, cap.radius * 2f);
            float half = Mathf.Max(0f, height * 0.5f - cap.radius);
            Vector3 axis = cap.direction == 0 ? t.right : (cap.direction == 2 ? t.forward : t.up);
            p0 = center + axis * half;
            p1 = center - axis * half;

            var ls = t.lossyScale;
            if (cap.direction == 0) radius = cap.radius * Mathf.Max(ls.y, ls.z);
            else if (cap.direction == 2) radius = cap.radius * Mathf.Max(ls.x, ls.y);
            else radius = cap.radius * Mathf.Max(ls.x, ls.z);
        }

[ServerRpc(RequireOwnership = false)]
public void SetPhaseServerRpc(PlayerPhase phase) { if (IsServer) SetPhase(phase); }

// Pre-spawn seed so clients spawn with phase=Match and skip "Lobby" log spam.
// Safe to call only on server before SpawnAsPlayerObject().
public void SeedPhasePreSpawnServer(PlayerPhase phase)
{
    if (!IsServer) return;
    initialPhase = phase;   // server OnNetworkSpawn will use this
    _phase = phase;
    _netPhase.Value = phase;
}
// Brief dev comment: Sets initial phase & NV prior to spawn so the spawn payload carries phase=Match.

        void SetPhase(PlayerPhase phase)
        {
            _phase = phase;
            if (IsServer) _netPhase.Value = phase;

            if (phase == PlayerPhase.Match)
            {
                if (IsServer && (SessionContext.Type == ServerType.OneVOne || SessionContext.Type == ServerType.TwoVTwo))
                {
                    ServerAutoEquipPrimary();
                }
                if (IsOwner)
                {
                    if (!_loadoutRequested)
                    {
                        // Skip client-side loadout send; server already applied pre-join.
                        Debug.Log("[PlayerNetwork] SetPhase Match; pre-join loadout already applied.");
                        _loadoutRequested = true;
                    }
                    OnActiveSlotChanged();
                }
            }
            else if (phase == PlayerPhase.Lobby && IsServer)
            {
                SetFrozenServer(false);
            }

            if (IsOwner)
            {
                _inputPaused = false;
            }
        }
        // Brief dev comment: Server writes the replicated phase; owner also kicks Cloud Save fetch if phase==Match.

        // Server-side helper used by controller to verify server actually has a loadout applied.
        public bool ServerHasValidLoadout()
        {
            if (!IsServer) return false;
            var lo = _netLoadout.Value;
            return lo.primary != 0 && lo.secondary != 0 && lo.melee != 0; // util may be None-less after sanitize
        }

        // ------- Freeze / Visibility -------

        public void SetFrozenServer(bool frozen)
        {
            if (!IsServer) return;

            _frozen = frozen;

            if (_rb && !_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }

            _rb.isKinematic = frozen;
            SetCollidersEnabled(!frozen);

            SetFrozenClientRpc(frozen);
        }

        [ClientRpc]
        void SetFrozenClientRpc(bool frozen)
        {
            _frozen = frozen;

            if (_rb && !_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }

            if (IsOwner)
            {
                _rb.isKinematic = frozen;
                _rb.useGravity = !frozen;
            }

            SetCollidersEnabled(!frozen);
        }

        public void SetVisible(bool visible)
        {
            if (_renderers == null || _renderers.Length == 0)
            {
                var root = modelRoot ? modelRoot : transform;
                _renderers = root.GetComponentsInChildren<Renderer>(true);
            }

            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i]) _renderers[i].enabled = visible;
        }

        // Force local model fully visible (guards against any fade components).
        void EnsureLocalModelVisible()
        {
            var span = GetModelRenderersSpan();
            if (span.Length == 0) return;
            var mpb = new MaterialPropertyBlock();
            for (int i = 0; i < span.Length; i++)
            {
                var r = span[i];
                if (!r) continue;
                r.GetPropertyBlock(mpb);
                // Push alpha to 1 on common color slots.
                if (r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor"))
                {
                    var c = r.sharedMaterial.GetColor("_BaseColor"); c.a = 1f;
                    mpb.SetColor("_BaseColor", c);
                }
                if (r.sharedMaterial && r.sharedMaterial.HasProperty("_Color"))
                {
                    var c2 = r.sharedMaterial.GetColor("_Color"); c2.a = 1f;
                    mpb.SetColor("_Color", c2);
                }
                r.SetPropertyBlock(mpb);
            }
        }

        void OnPlayerNameValueChanged(FixedString64Bytes _, FixedString64Bytes current)
        {
            if (current.IsEmpty) return;
            CacheIdentityFor(OwnerClientId, current.ToString(), null);
        }

        void OnPlayerIconValueChanged(FixedString128Bytes _, FixedString128Bytes current)
        {
            if (current.IsEmpty) return;
            CacheIdentityFor(OwnerClientId, null, current.ToString());
        }

        void CacheLocalIdentitySnapshot()
        {
            var name = Game.Services.PlayerIdentityState.LocalDisplayName;
            var icon = Game.Services.PlayerIdentityState.LocalIconId;
            CacheIdentityFor(OwnerClientId, name, icon);
        }

        static void CacheIdentityFor(ulong clientId, string name, string iconId)
        {
            if (!string.IsNullOrWhiteSpace(name))
                s_lastKnownNames[clientId] = name.Trim();
            if (!string.IsNullOrWhiteSpace(iconId))
                s_lastKnownIconIds[clientId] = iconId.Trim();
        }

        void SetCollidersEnabled(bool enabled)
        {
            if (_colliders == null || _colliders.Length == 0)
                _colliders = GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < _colliders.Length; i++)
                if (_colliders[i]) _colliders[i].enabled = enabled;
        }

    public TeamId GetTeam() => _team.Value;

    // === UI accessors (read-only) ===
    /// <summary>Current equipped slot (0=Primary, 1=Secondary, 2=Melee, 3=Utility) for local UI.</summary>
    public Game.Net.WeaponSlot GetActiveSlot() => (Game.Net.WeaponSlot)_activeSlot.Value;

    /// <summary>Replicated loadout so UI can show names without waiting for per-slot equip.</summary>
    public Game.Net.NetLoadout GetCurrentNetLoadout() => _netLoadout.Value;
        public void SetTeam(TeamId team) { if (IsServer) _team.Value = team; }

        public float GetHealth() => _health.Value;
        public void SetHealth(float health)
        {
            if (IsServer) _health.Value = Mathf.Clamp(health, 0f, 100f);
        }

        public CombatStats GetCombatStats() => _combatStats.Value;

        public void ApplyHealthDelta(float delta, PlayerNetwork attacker)
        {
            if (!IsServer) return;
            if (Mathf.Approximately(delta, 0f)) return;

            float previous = _health.Value;
            float target = Mathf.Clamp(previous + delta, 0f, 100f);
            if (Mathf.Approximately(previous, target)) return;

            _health.Value = target;

            // Damage actually applied this tick (positive when health decreased).
            float damageApplied = Mathf.Max(0f, previous - target);
            bool tookDamage = damageApplied > 0f;
            int damageInt = tookDamage ? Mathf.RoundToInt(damageApplied) : 0;
            bool died = previous > 0f && target <= 0f;

            Debug.Log(
                $"[Weapons] Health delta cid={OwnerClientId} delta={delta} applied={damageApplied:0} prev={previous:0} now={target:0} attackerCid={(attacker?attacker.OwnerClientId:ulong.MaxValue)} died={died}");

            if (died)
            {
                var victimStats = _combatStats.Value;
                victimStats.deaths = SafeAddUShort(victimStats.deaths, 1);
                _combatStats.Value = victimStats;
            }

            // Only award credit for real damage (no self-damage, no pure heals).
            if (attacker && attacker != this && tookDamage)
            {
                attacker.RegisterDamageCredit(damageInt, died);
            }

            if (died)
            {
                // Send the true applied damage into the kill recap instead of the requested delta.
                NotifyKilledServer(attacker, damageApplied);
            }
        }

        internal void RegisterDamageCredit(int damageAmount, bool registerKill)
        {
            if (!IsServer) return;
            if (damageAmount <= 0 && !registerKill) return;

            var stats = _combatStats.Value;
            if (damageAmount > 0)
                stats.damage = SafeAddInt(stats.damage, damageAmount);
            if (registerKill)
                stats.kills = SafeAddUShort(stats.kills, 1);
            _combatStats.Value = stats;
        }

        static ushort SafeAddUShort(ushort current, int delta)
        {
            int sum = current + delta;
            if (sum < 0) return 0;
            if (sum > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)sum;
        }

        static int SafeAddInt(int current, int delta)
        {
            long sum = (long)current + delta;
            if (sum < int.MinValue) return int.MinValue;
            if (sum > int.MaxValue) return int.MaxValue;
            return (int)sum;
        }

        /// Server helper to auto-equip primary at round start.
        public void ServerAutoEquipPrimary()
        {
            if (!IsServer) return;
            Debug.Log($"[Weapons] ServerAutoEquipPrimary cid={OwnerClientId}");

            // Always sanitize first so we never carry "None" into equip.
            ServerEnsureValidLoadoutAndHealth(sanitizeOnly: true);

            var wp = GetComponent<Game.Net.Weapons.WeaponPrimaryController>();
            if (!wp) return;

            var net = _netLoadout.Value;
            var pt = (PrimaryType)net.primary;
            if (pt == PrimaryType.None) return; // After sanitize this should never happen.

            _activeSlot.Value = 0; // Primary first
            wp.Equip(pt, null);    // server path equips and rebuilds views
        }
        // Previously added helper remains, but now we sanitize before using the replicated loadout【turn42file7†PlayerNetwork.cs†L65-L85】.

        /// <summary>
        /// Server-only guarantee: fill any empty slots with defaults (AR/Pistol/Knife/Grenade),
        /// ensure health is 100, and keep ActiveSlot on a valid weapon.
        /// </summary>
        public void ServerEnsureValidLoadoutAndHealth(bool sanitizeOnly = false)
        {
            if (!IsServer) return;

            var net = _netLoadout.Value;
            bool changed = false;

            // Defaults mandated by design: AR / Pistol / Knife / Grenade
            if ((PrimaryType)net.primary == PrimaryType.None)
            {
                net.primary = (byte)PlayerLoadout.Default.Primary; // expected AR
                if ((PrimaryType)net.primary == PrimaryType.None) net.primary = (byte)PrimaryType.AR; // correct enum
                changed = true;
            }
            if ((SecondaryType)net.secondary == SecondaryType.None)
            {
                net.secondary = (byte)PlayerLoadout.Default.Secondary; // expected Pistol
                if ((SecondaryType)net.secondary == SecondaryType.None) net.secondary = (byte)SecondaryType.Pistol;
                changed = true;
            }
            // Project has no MeleeType enum; use byte convention (0=unset, 1=Knife).
            if (net.melee == 0)
            {
                net.melee = 1; // Knife
                changed = true;
            }
            if ((UtilityType)net.util == UtilityType.None)
            {
                net.util = (byte)PlayerLoadout.Default.Utility; // expected Grenade
                if ((UtilityType)net.util == UtilityType.None) net.util = (byte)UtilityType.Grenade;
                changed = true;
            }

            if (changed) _netLoadout.Value = net;

            if (changed || !sanitizeOnly)
            {
                Debug.Log($"[PlayerNetwork] Loadout validated/set: P={(PrimaryType)net.primary} S={(SecondaryType)net.secondary} M={(net.melee == 1 ? "Knife" : "None")} U={(UtilityType)net.util} for cid={OwnerClientId} (changed={changed})");
            }

            if (sanitizeOnly) return;

            _activeSlot.Value = 0;
            var wp = GetComponent<Game.Net.Weapons.WeaponPrimaryController>();
            if (wp) wp.Equip((PrimaryType)net.primary, null);

            var ws = GetComponent<Game.Net.Weapons.WeaponSecondaryController>();
            if (ws && (SecondaryType)net.secondary != SecondaryType.None) ws.Equip((SecondaryType)net.secondary, null);

            var wm = GetComponent<Game.Net.Weapons.WeaponMeleeController>();
            if (wm) wm.Equip();

            _health.Value = 100f;
        }
// Brief dev comment: replace nonexistent MeleeType enum with byte convention and fix PrimaryType.AR.
        // Server-only fallback that guarantees "no None" and a healthy spawn.

// --- FOV / LOS enable/disable helpers (Match only) ---
void SetFovAndLosEnabled(bool enabled)
{
    if (!IsOwner) return;

    // FOV mesh on the local player
    var fov = GetComponent<FovMesh>();
    var losLight = GetComponent<PlayerLosLight>();

    if (enabled)
    {
        // Ensure camera is bound before setting up LOS
        if (_cam == null) TryBindCamera();

        if (!fov) fov = gameObject.AddComponent<FovMesh>();
        // Same configuration you currently set on spawn:
        fov.radiusMeters = 12f;
        fov.rayCount = 220;
        fov.occluderMask = LayerMask.GetMask("Occluder", "OccluderExtra");
        fov.showFill = true;
        fov.fillColor = new Color(0.95f, 0.97f, 1.0f, 0.45f);
        fov.fillIntensity = 1.15f;
        fov.edgeFeather = 0.15f;
        fov.visualColor = new Color(0.92f, 0.97f, 1.0f, 0.62f);
        fov.follow = modelRoot ? modelRoot : transform;
        fov.enabled = true; // Explicitly enable

        // Ensure the FOV visual rides at the player's feet and does not inherit yaw.
        var fovConstraint = GetComponent<FovVisualConstraintBinder>();
        if (!fovConstraint) fovConstraint = gameObject.AddComponent<FovVisualConstraintBinder>();
        fovConstraint.Source = modelRoot ? modelRoot : transform;

        if (!losLight) losLight = gameObject.AddComponent<PlayerLosLight>();
        losLight.fovSource = fov;
        losLight.intensity = 1.3f;
        losLight.rangeScale = 0.9f;
        losLight.castShadows = true;
        losLight.enabled = true;

        // Install camera LOS components
        if (_cam != null)
        {
            var los = _cam.GetComponent<LineOfSightTransparency>();
            if (!los) los = _cam.gameObject.AddComponent<LineOfSightTransparency>();

            // Match phase keeps your original behavior, but we explicitly set the knobs:
            los.target = transform;
            los.occluderLayers = LayerMask.GetMask("Occluder", "OccluderExtra");
            los.occludedAlpha = 0.6f; // 40% transparent everywhere

            los.enabled = true;

            // Prevent occlusion culling issues with our transparent fades
            _cam.useOcclusionCulling = false;

            // Ensure the fog overlay plane exists
            FogOfWarOverlayPlane.InstallFor(_cam);
        }
    }
    else
    {
        if (losLight) losLight.enabled = false;
        if (fov) fov.enabled = false;

        // Remove fog overlay + line-of-sight transparency from camera
        RemoveLosFromCamera();
    }
}

void RemoveLosFromCamera()
{
    if (_cam == null)
        return;

    // Kill the fog overlay child, if present
    var overlay = _cam.GetComponentInChildren<FogOfWarOverlayPlane>(true);
    if (overlay) Destroy(overlay.gameObject);

    // Remove camera LOS transparency in lobby so players never get faded
    var los = _cam.GetComponent<LineOfSightTransparency>();
    if (los) Destroy(los);
}

// Keep simple camera→target occluder transparency active regardless of phase.
// Lobby = only "Occluder" layer; Match = "Occluder" + "OccluderExtra".
void EnsureCameraOccluderTransparencyActive(bool lobbyOnly = false)
{
    if (!IsOwner) return;
    if (_cam == null) TryBindCamera();
    if (_cam == null) return;

    var los = _cam.GetComponent<LineOfSightTransparency>();
    if (!los) los = _cam.gameObject.AddComponent<LineOfSightTransparency>();

    los.target = transform;
    los.occludedAlpha = 0.6f; // 40% transparent
    los.occluderLayers = lobbyOnly
        ? LayerMask.GetMask("Occluder")
        : LayerMask.GetMask("Occluder", "OccluderExtra");
    los.enabled = true;

    // Avoid culling fights with faded occluders.
    _cam.useOcclusionCulling = false;
}

        void SetSprint(bool on)
        {
            if (IsSprinting == on) return;
            IsSprinting = on;
            SprintChanged?.Invoke(on);

            // Pause reload on sprint: forward to weapon controller if present.
            var wp = GetComponent<WeaponPrimaryController>();
            if (wp) wp.OnSprintChanged(on);
        }
    }
}