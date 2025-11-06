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
    public sealed class PlayerNetwork : NetworkBehaviour
    {
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

        // Simple server rate-limits
        float _lastSwitchServerTime, _lastThrowServerTime;
        const float k_MinSwitchInterval = 0.08f;   // ~10/s
        const float k_MinThrowInterval  = 0.5f;    // guard spam
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
        [SerializeField] LayerMask groundMask = ~0;
        [SerializeField] float groundSnapUp = 2.0f;
        [SerializeField] float groundSnapDown = 6.0f;
        [SerializeField] float groundSkin = 0.02f;

        [Header("State")]
        [SerializeField] PlayerPhase initialPhase = PlayerPhase.Lobby;

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
        NetworkVariable<float> _netYaw = new();
        NetworkVariable<Vector3> _netVelocity = new();
        NetworkVariable<bool> _netIsDashing = new();
        readonly NetworkVariable<FixedString64Bytes> _playerName =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        const int MaxDisplayNameLength = 32;
        readonly NetworkVariable<FixedString128Bytes> _playerIconId =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        const int MaxIconIdLength = 96;

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
        InputAction _aScoreboard;
        public bool IsSprinting { get; private set; }
        public bool IsDashing  { get; private set; }
        public event System.Action<bool> SprintChanged;
        public event System.Action<bool> DashChanged;
        InputAction _aSlot1, _aSlot2, _aSlot3, _aThrow;
        Vector2 _inMove, _inMouse; bool _inSprint;

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
            if (!_rb)
            {
                _rb = GetComponent<Rigidbody>();
                _capsule = GetComponent<CapsuleCollider>();
            }

            _phase = initialPhase;

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
            _health.OnValueChanged += OnHealthChanged;
            _combatStats.OnValueChanged += OnCombatStatsChanged;

            // Client: send saved loadout to server after spawn
            if (IsOwner)
                StartCoroutine(CoLoadAndSendLoadout());

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

                // Install client-side LOS FOV visualization for local player.
                var fov = gameObject.GetComponent<FovMesh>();
                if (!fov) fov = gameObject.AddComponent<FovMesh>();
                fov.radiusMeters = 12f;                                  // LOS radius
                fov.rayCount = 220;                                      // tuned for perf
                // Detect both LOS layers.
                fov.occluderMask = LayerMask.GetMask("Occluder", "OccluderExtra");
                fov.showFill = true;
                fov.fillColor = new Color(0.95f, 0.97f, 1.0f, 0.45f); // bright inside
                fov.fillIntensity = 1.15f;
                fov.edgeFeather = 0.15f;
                fov.visualColor = new Color(0.92f, 0.97f, 1.0f, 0.62f);
                // Anchor FOV to visual root if provided so the stencil sits on the model.
                fov.follow = modelRoot ? modelRoot : transform;

                var losLight = GetComponent<PlayerLosLight>();
                if (!losLight) losLight = gameObject.AddComponent<PlayerLosLight>();
                losLight.fovSource = fov;
                losLight.intensity = 1.3f;
                losLight.rangeScale = 0.9f;
                losLight.castShadows = true;

                FogOfWarOverlayPlane.InstallFor(Camera.main);
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
                _inputPaused = false;

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
                _netYaw.OnValueChanged += OnYawChanged;
                _netVelocity.OnValueChanged += OnVelocityChanged;
                _netIsDashing.OnValueChanged += OnDashingChanged;
            }
        }

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

        public override void OnNetworkDespawn()
        {
            _map?.Disable();
            if (_aDash  != null) _aDash.performed  -= OnDashPerformed;
            if (_aSlot1 != null) _aSlot1.performed -= _ => RequestSwitchSlot(0);
            if (_aSlot2 != null) _aSlot2.performed -= _ => RequestSwitchSlot(1);
            if (_aSlot3 != null) _aSlot3.performed -= _ => RequestSwitchSlot(2);
            if (_aThrow != null) _aThrow.performed -= _ => RequestThrowUtility();
            _map = null; _aMove = _aMouse = _aSprint = _aDash = null; _aSlot1 = _aSlot2 = _aSlot3 = _aThrow = null;

            _activeSlot.OnValueChanged -= (_, __) => OnActiveSlotChanged();
            _playerName.OnValueChanged -= OnPlayerNameValueChanged;
            _playerIconId.OnValueChanged -= OnPlayerIconValueChanged;
            _health.OnValueChanged -= OnHealthChanged;
            _combatStats.OnValueChanged -= OnCombatStatsChanged;

            if (IsOwner)
            {
                _statsPendingForCloud = _combatStats.Value;
                QueueStatsFlush(true);
            }

            if (useLegacyStateReplication)
            {
                _netPosition.OnValueChanged -= OnPositionChanged;
                _netYaw.OnValueChanged -= OnYawChanged;
                _netVelocity.OnValueChanged -= OnVelocityChanged;
                _netIsDashing.OnValueChanged -= OnDashingChanged;
            }
        }

        // ==== HUD binding API (for PlayerHUDBinder) ====
        public void AssignHud(Image sprintFillUI, TMP_Text sprintLabelUI, Image dashFillUI, TMP_Text dashLabelUI, Image healthFillUI = null, TMP_Text healthLabelUI = null)
        {
            if (sprintFillUI) sprintFill = sprintFillUI;
            if (sprintLabelUI) sprintLabel = sprintLabelUI;
            if (dashFillUI) dashFill = dashFillUI;
            if (dashLabelUI) dashLabel = dashLabelUI;
            if (healthFillUI) healthFill = healthFillUI;
            if (healthLabelUI) healthLabel = healthLabelUI;
            UpdateHealthUI(_health.Value);
        }

        public void AssignHud(Component root)
        {
            if (!root) return;
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

            UpdateHealthUI(_health.Value);
        }

        public void ClearHud()
        {
            sprintFill = null; sprintLabel = null; dashFill = null; dashLabel = null; healthFill = null; healthLabel = null;
        }

        void OnHealthChanged(float previous, float current)
        {
            UpdateHealthUI(current);
        }

        void UpdateHealthUI(float current)
        {
            if (healthFill)
                healthFill.fillAmount = Mathf.Clamp01(current <= 0f ? 0f : current / 100f);

            if (healthLabel)
            {
                int value = Mathf.Clamp(Mathf.RoundToInt(current), 0, 999);
                healthLabel.text = value.ToString("D3");
            }
        }

        void OnCombatStatsChanged(CombatStats previous, CombatStats current)
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

                var task = CloudSaveClient.AppendStatsAsync(deltaKills, deltaDeaths, deltaDamage);
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

        static ClientRpcParams TargetClientParams(ulong clientId)
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
        // ================================================
// ===== Weapons: equip, switch, throw (server authoritative) =====

void RequestSwitchSlot(byte slot)
{
    if (_inputPaused) return;
    if (_phase != PlayerPhase.Match) return;
    if (slot > 3) return;
    RequestSwitchSlotServerRpc(slot);
}

[ServerRpc]
void RequestSwitchSlotServerRpc(byte slot, ServerRpcParams p = default)
{
    if (_phase != PlayerPhase.Match) return;
    if (slot > 3) return;

    // Min interval
    if (Time.time - _lastSwitchServerTime < k_MinSwitchInterval) return;
    _lastSwitchServerTime = Time.time;

    // Validate against equipped loadout: Primary/Secondary always valid; Melee always allowed; Utility only if set
    bool allowed =
        slot == 0 ||
        slot == 1 ||
        slot == 2 || // melee fixed
        (slot == 3 && _netLoadout.Value.util != (byte)UtilityType.None);

    if (!allowed) return;

    _activeSlot.Value = slot;
#if UNITY_EDITOR
    Debug.Log($"[Weapons] Active slot -> {slot}");
#endif
    // TODO: later handle firing state teardown and equip animations.
}

void RequestThrowUtility()
{
    if (_inputPaused) return;
    RequestThrowUtilityServerRpc();
}

[ServerRpc]
void RequestThrowUtilityServerRpc(ServerRpcParams p = default)
{
    if (_netLoadout.Value.util == (byte)UtilityType.None) return;

    if (Time.time - _lastThrowServerTime < k_MinThrowInterval) return;
    _lastThrowServerTime = Time.time;

    // For now, just log. We will implement grenade entity later.
#if UNITY_EDITOR
    Debug.Log($"[Weapons] Throw utility: {(UtilityType)_netLoadout.Value.util}");
#endif
}

void OnActiveSlotChanged()
{
    // Owner requests equip. Remotes wait for server fan-out.
    if (!IsOwner) return;

    if (_activeSlot.Value == 0)
    {
        var wp = GetComponent<WeaponPrimaryController>();
        if (wp != null)
        {
            var pt = (Game.Net.PrimaryType)_netLoadout.Value.primary;
            wp.Equip(pt, null); // server validates; stats default for now
        }
    }
}
// Fixes "Only the owner can invoke a ServerRpc…" by stopping non-owners from calling it.
// ===== end weapons =====

        void SetupInputAndCamera()
        {
            _map = new InputActionMap("Player");

            _aMove = _map.AddAction(name: "Move", type: InputActionType.Value, expectedControlLayout: "Vector2");
            _aMove.AddCompositeBinding("2DVector")
                  .With("Up", "<Keyboard>/w")
                  .With("Down","<Keyboard>/s")
                  .With("Left","<Keyboard>/a")
                  .With("Right","<Keyboard>/d");

            _aMouse  = _map.AddAction(name: "MousePos", type: InputActionType.Value, binding: "<Pointer>/position");
            var aFire   = _map.AddAction(name: "Fire", type: InputActionType.Button, binding: "<Mouse>/leftButton");
            var aReload = _map.AddAction(name: "Reload", type: InputActionType.Button, binding: "<Keyboard>/r");

            aFire.performed += _ => GetComponent<WeaponPrimaryController>()?.FireHeld(true);
            aFire.canceled  += _ => GetComponent<WeaponPrimaryController>()?.FireHeld(false);
            aReload.performed += _ => GetComponent<WeaponPrimaryController>()?.RequestReload();
            _aSprint = _map.AddAction(name: "Sprint", type: InputActionType.Button, binding: "<Keyboard>/leftShift");
            _aDash   = _map.AddAction(name: "Dash", type: InputActionType.Button, binding: "<Keyboard>/space");

            _aSprint.performed += _ => SetSprint(true);
            _aSprint.canceled  += _ => SetSprint(false);

            // Weapon inputs
            _aSlot1 = _map.AddAction(name: "Slot1", type: InputActionType.Button, binding: "<Keyboard>/1");
            _aSlot2 = _map.AddAction(name: "Slot2", type: InputActionType.Button, binding: "<Keyboard>/2");
            _aSlot3 = _map.AddAction(name: "Slot3", type: InputActionType.Button, binding: "<Keyboard>/3");
            _aThrow = _map.AddAction(name: "Throw", type: InputActionType.Button, binding: "<Keyboard>/g");
            _aScoreboard = _map.AddAction(name: "Scoreboard", type: InputActionType.Button, binding: "<Keyboard>/tab");

            _aDash.performed += OnDashPerformed;
            // forward dash state to weapon controller via OnDashingChanged callback already patched.
            _aSlot1.performed += _ => RequestSwitchSlot(0);
            _aSlot2.performed += _ => RequestSwitchSlot(1);
            _aSlot3.performed += _ => RequestSwitchSlot(2);
            _aThrow.performed += _ => RequestThrowUtility();
            _aScoreboard.performed += OnScoreboardPerformed;
            _aScoreboard.canceled  += OnScoreboardCanceled;

            _map.Enable();
            TryBindCamera();
            TryBindScoreboard();
        }

        void OnDashPerformed(InputAction.CallbackContext ctx)
        {
            if (_inputPaused) return;
            _dashQueuedUntil = Time.time + dashInputBuffer;
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
            if (paused) _map?.Disable(); else _map?.Enable();
            if (paused) ShowScoreboard(false);
        }

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
                _isoCam.enabled = true; // ensure camera is active after (re)spawn — avoids staying on spawn-select view

                var los = _cam.GetComponent<LineOfSightTransparency>() ?? _cam.gameObject.AddComponent<LineOfSightTransparency>();
                los.target = transform;

// Prevent baked/dynamic occlusion from hiding players behind now-transparent occluders.
                _cam.useOcclusionCulling = false;

                FogOfWarOverlayPlane.InstallFor(_cam); // guarantees culling mask includes LOS layer once camera exists
// Disables camera occlusion culling when LOS transparency is active so faded occluders can't fully hide the player.
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
        void SubmitPlayerIdentityServerRpc(FixedString64Bytes alias, FixedString64Bytes iconId, ServerRpcParams rpcParams = default)
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

        static FixedString64Bytes SanitizeIconId(FixedString64Bytes iconId)
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
            return new FixedString64Bytes(new string(buffer.Slice(0, len)));
        }

        void LateUpdate()
        {
            if (!IsOwner) return;
            if (_isoCam == null && _camBindTries < 60) { _camBindTries++; TryBindCamera(); }
            if (_scoreboard == null && _scoreboardBindTries < 120) { _scoreboardBindTries++; TryBindScoreboard(); }
            if (_inputPaused) { UpdateUI(); return; }
        }

        void Update()
        {
            if (!IsOwner) { InterpolateRemotePlayer(); return; }
            if (_inputPaused) { UpdateUI(); return; }

            _inMove   = _aMove?.ReadValue<Vector2>() ?? Vector2.zero;
            _inMouse  = _aMouse?.ReadValue<Vector2>() ?? Vector2.zero;
            _inSprint = _aSprint != null && _aSprint.IsPressed();

            UpdateUI();
        }

        // Load CloudSave -> send to server (owner only). Validated server-side.
        System.Collections.IEnumerator CoLoadAndSendLoadout()
        {
            // Prefer cached value from SessionContext to avoid a network read
            PlayerLoadout lo = SessionContext.TryGetLoadout(out var cached) ? cached : PlayerLoadout.Default;

            // If not cached, try CloudSave
            if (!SessionContext.TryGetLoadout(out _))
            {
                var task = CloudSaveClient.LoadLoadoutAsync(PlayerLoadout.Default);
                while (!task.IsCompleted) yield return null;
                lo = task.Result;
                SessionContext.SetLoadout(lo);
            }

            _myLoadout = lo;
            var net = NetLoadout.From(lo);
            EquipLoadoutServerRpc(net.primary, net.secondary, net.util);
        }

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

            float yaw = transform.eulerAngles.y;
            if (_cam)
            {
                var ray = _cam.ScreenPointToRay(_inMouse);
                Vector3 aimPoint = transform.position;
                bool aimResolved = false;

                if (Physics.Raycast(ray, out var hit, 500f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    aimPoint = hit.point;
                    aimResolved = true;
                }
                else
                {
                    var plane = new Plane(Vector3.up, transform.position);
                    if (plane.Raycast(ray, out float enter))
                    {
                        aimPoint = ray.GetPoint(enter);
                        aimResolved = true;
                    }
                }

                if (aimResolved)
                {
                    var dir = aimPoint - transform.position; dir.y = 0f;
                    if (dir.sqrMagnitude > 0.0001f)
                        yaw = Quaternion.LookRotation(dir.normalized, Vector3.up).eulerAngles.y;
                }
            }

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

            var targetRot = Quaternion.Euler(0f, yaw, 0f);
            var nextRot = Quaternion.RotateTowards(transform.rotation, targetRot, 1080f * dt);
            if (_rb) _rb.MoveRotation(nextRot);
            transform.rotation = nextRot;

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
        void EquipLoadoutServerRpc(byte primary, byte secondary, byte util, ServerRpcParams p = default)
        {
            // Validate ranges
            if (primary > (byte)PrimaryType.Sniper) primary = 0;
            if (secondary > (byte)SecondaryType.MachinePistol) secondary = 0;
            if (util > (byte)UtilityType.Stun) util = (byte)UtilityType.Grenade;

            _netLoadout.Value = new NetLoadout { primary = primary, secondary = secondary, util = util };

#if UNITY_EDITOR
            Debug.Log($"[PlayerNetwork] Equipped loadout P={(PrimaryType)primary} S={(SecondaryType)secondary} U={(UtilityType)util} for {OwnerClientId}");
#endif

            // TODO: hook your weapon/inventory system here using _netLoadout.Value.ToModel()

            if (SessionContext.Type == ServerType.OneVOne || SessionContext.Type == ServerType.TwoVTwo)
            {
                ServerAutoEquipPrimary();
            }
        }

        void OnPositionChanged(Vector3 _, Vector3 newVal)
        {
            if (IsOwner) return;
            AddStateToBuffer(newVal, _netYaw.Value, _netVelocity.Value, _netIsDashing.Value);
        }

        void OnYawChanged(float _, float newVal)
        {
            if (IsOwner) return;
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

        void SetPhase(PlayerPhase phase)
        {
            _phase = phase;

            if (phase == PlayerPhase.Match)
            {
                if (IsServer && (SessionContext.Type == ServerType.OneVOne || SessionContext.Type == ServerType.TwoVTwo))
                {
                    ServerAutoEquipPrimary();
                }
                if (IsOwner)
                {
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

            bool tookDamage = delta < 0f;
            int damageInt = tookDamage ? Mathf.RoundToInt(-delta) : 0;
            bool died = previous > 0f && target <= 0f;

            if (died)
            {
                var victimStats = _combatStats.Value;
                victimStats.deaths = SafeAddUShort(victimStats.deaths, 1);
                _combatStats.Value = victimStats;
            }

            if (attacker && attacker != this && tookDamage)
            {
                attacker.RegisterDamageCredit(damageInt, died);
            }

            if (died)
            {
                NotifyKilledServer(attacker, Mathf.Max(0f, -delta));
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
            var wp = GetComponent<Game.Net.Weapons.WeaponPrimaryController>();
            if (!wp) return;

            var net = _netLoadout.Value;
            var pt = (PrimaryType)net.primary;

            if (pt == PrimaryType.None)
            {
                pt = PlayerLoadout.Default.Primary;
                if (pt == PrimaryType.None) return;
                net.primary = (byte)pt;
                _netLoadout.Value = net;
            }

            _activeSlot.Value = 0; // Primary
            wp.Equip(pt, null); // server path equips and rebuilds views
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