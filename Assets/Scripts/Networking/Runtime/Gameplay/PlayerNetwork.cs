// Assets/Scripts/Networking/Runtime/Gameplay/PlayerNetwork.cs
// Server-auth movement with stamina + dash, interpolation for remotes,
// plus freeze/visibility controls used by Match1v1Controller.

using System;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

namespace Game.Net
{
    public enum PlayerPhase : byte { Lobby = 0, Match = 1 }

    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class PlayerNetwork : NetworkBehaviour
    {
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

        [Header("Visual Root (optional)")]
        [SerializeField] Transform modelRoot;

        Rigidbody _rb;
        CapsuleCollider _capsule;

        Renderer[] _renderers;
        Collider[] _colliders;

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

        InputActionMap _map;
        InputAction _aMove, _aMouse, _aSprint, _aDash;
        Vector2 _inMove, _inMouse; bool _inSprint;

        float _stamina; float _sprintRegenResumeAt;

        bool _isDashing; float _dashStartTime, _dashEndTime, _dashReadyAt, _dashYaw; Vector3 _dashDirXZ; float _dashQueuedUntil;

        Camera _cam; IsometricCamera _isoCam; int _camBindTries;

        bool _inputPaused;
        bool _frozen;

        private readonly NetworkVariable<TeamId> _team = new(TeamId.A);

        private readonly NetworkVariable<float> _health = new(100f);

        public override void OnNetworkSpawn()
        {
            if (!_rb)
            {
                _rb = GetComponent<Rigidbody>();
                _capsule = GetComponent<CapsuleCollider>();
            }

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
                SetInputPaused(false);
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
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            var root = modelRoot ? modelRoot : transform;
            _renderers = root.GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);

            _stamina = sprintStaminaMax;
        }

        public override void OnNetworkDespawn()
        {
            _map?.Disable();
            if (_aDash != null) _aDash.performed -= OnDashPerformed;
            _map = null; _aMove = _aMouse = _aSprint = _aDash = null;

            if (useLegacyStateReplication)
            {
                _netPosition.OnValueChanged -= OnPositionChanged;
                _netYaw.OnValueChanged -= OnYawChanged;
                _netVelocity.OnValueChanged -= OnVelocityChanged;
                _netIsDashing.OnValueChanged -= OnDashingChanged;
            }
        }

        // ==== HUD binding API (for PlayerHUDBinder) ====
        public void AssignHud(Image sprintFillUI, TMP_Text sprintLabelUI, Image dashFillUI, TMP_Text dashLabelUI)
        {
            sprintFill = sprintFillUI;
            sprintLabel = sprintLabelUI;
            dashFill = dashFillUI;
            dashLabel = dashLabelUI;
        }

        public void AssignHud(Component root)
        {
            if (!root) return;
            sprintFill ??= root.GetComponentInChildren<Image>(true);
            sprintLabel ??= root.GetComponentInChildren<TMP_Text>(true);

            foreach (var img in root.GetComponentsInChildren<Image>(true))
                if (img && img.gameObject.name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0) { dashFill = img; break; }

            foreach (var txt in root.GetComponentsInChildren<TMP_Text>(true))
                if (txt && txt.gameObject.name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0) { dashLabel = txt; break; }
        }

        public void ClearHud()
        {
            sprintFill = null; sprintLabel = null; dashFill = null; dashLabel = null;
        }
        // ================================================

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
            _aSprint = _map.AddAction(name: "Sprint", type: InputActionType.Button, binding: "<Keyboard>/leftShift");
            _aDash   = _map.AddAction(name: "Dash", type: InputActionType.Button, binding: "<Keyboard>/space");

            _aDash.performed += OnDashPerformed;
            _map.Enable();

            TryBindCamera();
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
            }
        }

        void LateUpdate()
        {
            if (!IsOwner) return;
            if (_inputPaused) { UpdateUI(); return; }
            if (_isoCam == null && _camBindTries < 60) { _camBindTries++; TryBindCamera(); }
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

        void FixedUpdate()
        {
            if (!IsOwner) return;
            if (_rb == null) return;

            // Do not write velocity to kinematic bodies.
            if (_rb.isKinematic) return;

            if (_inputPaused)
            {
                var v0 = _rb.linearVelocity; v0.x = 0f; v0.z = 0f; _rb.linearVelocity = v0;
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
                if (Physics.Raycast(ray, out var hit, 500f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    var dir = hit.point - transform.position; dir.y = 0f;
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
                transform.rotation = Quaternion.Euler(0f, _dashYaw, 0f);

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

            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, yaw, 0f), 1080f * dt);

            float speedMove = moveSpeed * (wantSprint ? sprintMultiplier : 1f);
            var vel = _rb.linearVelocity;
            vel.x = wish.x * speedMove;
            vel.z = wish.z * speedMove;
            _rb.linearVelocity = vel;

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
            if (phase == PlayerPhase.Lobby && IsServer) SetFrozenServer(false);
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
            if (IsServer) _health.Value = health;
        }
    }
}