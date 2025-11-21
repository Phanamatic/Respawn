using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// Server-authoritative utility/throwable weapon controller living on the player.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class WeaponUtilityController : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] PlayerWeaponSockets sockets;

        [Header("UI Replication")]
        public NetworkVariable<int> ammoCount = new(writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<FixedString64Bytes> equippedWeaponName = new(writePerm: NetworkVariableWritePermission.Server);

        // Replicate selected utility type so clients can build local view
        readonly NetworkVariable<byte> _netUtilityType =
            new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        [Header("Throwable Prefabs")]
        [SerializeField] GameObject fragGrenadePrefab;
        [SerializeField] GameObject smokePrefab;
        [SerializeField] GameObject stunPrefab;

        [Header("Utility View Prefabs (for hand mount)")]
        [SerializeField] GameObject grenadeViewPrefab;
        [SerializeField] GameObject smokeViewPrefab;
        [SerializeField] GameObject stunViewPrefab;

        [Header("Settings")]
        [SerializeField] float throwForce = 20f;
        [SerializeField] float throwAngle = 30f; // degrees upward

        Game.Net.PlayerNetwork _player;
        bool _hasEquippedUtility;
        // Cached utility type used for local view rebuilds; driven by owner requests + server RPC.
        Game.Net.UtilityType _lastEquippedUtilityType = Game.Net.UtilityType.None;
        WeaponView _view;
        LineRenderer _arcRenderer;
        Transform _landingMarker;
        float _lastServerThrowTime;

        static readonly Color k_ArcColor = new(0.85f, 0.95f, 1f, 0.8f);
        const int k_ArcSegments = 18;
        const float k_PreviewDuration = 2.5f;

// Brief dev comment: _lastEquippedUtilityType lets the owner build the correct view immediately without waiting on _netUtilityType replication.

        void Awake()
        {
            _player = GetComponent<Game.Net.PlayerNetwork>();
            if (!sockets) sockets = GetComponent<PlayerWeaponSockets>(); // runtime fallback
        }

        // ====== API ======
        public void Equip(Game.Net.UtilityType utilityType)
        {
            if (!IsServer)
            {
                // Owner path: set local equip state + type up-front so immediate rebuild can succeed.
                _hasEquippedUtility = (utilityType != Game.Net.UtilityType.None);
                _lastEquippedUtilityType = utilityType;
                RequestEquipServerRpc((byte)utilityType);
            }
            else
            {
                ServerEquip(utilityType);
            }
        }

// Brief dev comment: owner caches equip flag + type before the server reply so OnActiveSlotChanged → RebuildLocalViewImmediate() has the right data.

        public void RequestThrow()
        {
            if (!IsOwner) return;
            RequestThrowServerRpc();
        }

        // ====== Server logic ======
        [ServerRpc] void RequestEquipServerRpc(byte utility) { ServerEquip((Game.Net.UtilityType)utility); }

        void ServerEquip(Game.Net.UtilityType t)
        {
            Debug.Log($"[Utility][ServerEquip] owner={OwnerClientId} type={t}");

            _netUtilityType.Value = (byte)t;

            if (t == Game.Net.UtilityType.None)
            {
                Debug.LogWarning($"[Utility][ServerEquip] None -> disabling utility for owner={OwnerClientId}");
                ammoCount.Value = 0;
                equippedWeaponName.Value = "";
                _hasEquippedUtility = false;
                // Push cleared state so owners can tear down any stale local views.
                RebuildLocalViewClientRpc(_hasEquippedUtility, (byte)Game.Net.UtilityType.None);
                return;
            }

            ammoCount.Value = 2; // Default 2 per utility
            equippedWeaponName.Value = t.ToString();
            _hasEquippedUtility = true;

            Debug.Log($"[Utility][ServerEquip] set ammo={ammoCount.Value} name={equippedWeaponName.Value} -> rebuild local views");
            // Fan-out equip flag + type so clients rebuild with the correct prefab even if _netUtilityType hasn't replicated yet.
            RebuildLocalViewClientRpc(_hasEquippedUtility, (byte)t);
        }

        // ====== Client visuals ======
        [ClientRpc] void RebuildLocalViewClientRpc(bool hasEquipped, byte utilityType)
        {
            // Mirror server equip state and last equipped type locally, then rebuild.
            _hasEquippedUtility = hasEquipped;
            _lastEquippedUtilityType = (Game.Net.UtilityType)utilityType;
            RebuildLocalViewImmediate();
        }

        public void RebuildLocalViewImmediate()
        {
            if (!IsOwner) return;
            if (_player && _player.GetActiveSlot() != Game.Net.WeaponSlot.Utility)
            {
                Debug.Log("[Utility] Skip rebuild: slot not active.");
                return;
            }

            if (_view) Destroy(_view.gameObject);
            _view = null;

            Debug.Log($"[Utility][RebuildLocalViewImmediate] owner={OwnerClientId} hasEquipped={_hasEquippedUtility} netType={(Game.Net.UtilityType)_netUtilityType.Value} goActive={gameObject.activeInHierarchy} frame={Time.frameCount}");

            if (!_hasEquippedUtility)
            {
                Debug.LogWarning("[Utility] Abort: _hasEquippedUtility=false (parent="
                    + (transform.parent ? transform.parent.name : "<null>")
                    + ", children=" + transform.childCount
                    + ", frame=" + Time.frameCount + ")");
                return;
            }

            // Prefer the cached last-equipped type for local visuals; fall back to the networked value.
            var utilityType = _lastEquippedUtilityType;
            if (utilityType == Game.Net.UtilityType.None)
                utilityType = (Game.Net.UtilityType)_netUtilityType.Value;

            if (utilityType == Game.Net.UtilityType.None)
            {
                Debug.LogWarning("[Utility] Abort: utilityType=None");
                return;
            }

            if (!sockets)
            {
                Debug.LogWarning("[Utility] Abort: PlayerWeaponSockets missing on player. Cannot attach Utility WeaponView.");
                return;
            }
            if (!sockets.handMount)
            {
                Debug.LogWarning("[Utility] Abort: sockets.handMount not assigned. Cannot attach Utility WeaponView.");
                return;
            }

            GameObject viewPrefab = utilityType switch
            {
                Game.Net.UtilityType.Grenade => grenadeViewPrefab,
                Game.Net.UtilityType.Smoke   => smokeViewPrefab,
                Game.Net.UtilityType.Stun    => stunViewPrefab,
                _ => null
            };

            if (!viewPrefab)
            {
                Debug.LogWarning($"[Utility] Abort: No view prefab for {utilityType}. Assign in inspector.");
                return;
            }

            // Ensure only one weapon view exists under the Hand Mount
            int preChildren = sockets.handMount.childCount;
            for (int i = preChildren - 1; i >= 0; i--) Destroy(sockets.handMount.GetChild(i).gameObject);
            Debug.Log($"[Utility] Cleared handMount children: {preChildren} -> 0 (mount={sockets.handMount.name})");

            var go = Instantiate(viewPrefab);
            go.name = $"{utilityType}_View(Local)";
            _view = go.GetComponent<WeaponView>();

            var t = go.transform;

            // Bind Grip → Hand Mount
            t.SetParent(sockets.handMount, false);
            if (_view)
            {
                Debug.Log($"[Utility] Snapping view '{go.name}' grip={(bool)_view.grip} to mount={sockets.handMount.name}");
                _view.SnapGripTo(sockets.handMount);
            }
            else
            {
                Debug.LogWarning("[Utility] View prefab missing WeaponView component; falling back to raw align.");
                t.position = sockets.handMount.position; t.rotation = sockets.handMount.rotation;
            }

            // Default facing toward Front point (uses tip if present)
            if (_view && sockets.front)
            {
                Debug.Log($"[Utility] SnapAimTo front='{sockets.front.name}' using tip={(bool)_view.tip}");
                _view.SnapAimTo(sockets.front);
            }

            if (_view && sockets.equipStart && sockets.front)
            {
                Debug.Log($"[Utility] PlayEquipAnimation from='{sockets.equipStart.name}' to='{sockets.front.name}'");
                StartCoroutine(_view.PlayEquipAnimation(sockets.equipStart, sockets.front, 0.25f));
            }
            // Equip state cached locally for immediate rebuilds; networked value used as fallback.
        }

        [ServerRpc] void RequestThrowServerRpc(ServerRpcParams p = default)
        {
            if (!_hasEquippedUtility) return;
            if (ammoCount.Value <= 0) return;
            if (_player && _player.GetActiveSlot() != Game.Net.WeaponSlot.Utility) return;
            if (Time.time - _lastServerThrowTime < 0.1f) return;

            ammoCount.Value--;
            _lastServerThrowTime = Time.time;

            var utilityType = (Game.Net.UtilityType)_netUtilityType.Value;
            GameObject prefab = utilityType switch
            {
                Game.Net.UtilityType.Grenade => fragGrenadePrefab,
                Game.Net.UtilityType.Smoke => smokePrefab,
                Game.Net.UtilityType.Stun => stunPrefab,
                _ => null
            };

            if (!prefab)
            {
                Debug.LogWarning($"[WeaponUtility] No prefab for {utilityType}");
                return;
            }

            if (!TryGetThrowSolution(out var origin, out var velocity))
                return;

            var go = Instantiate(prefab, origin, Quaternion.LookRotation(velocity.normalized, Vector3.up));

            // Prevent immediate self-collisions on spawn.
            var projCols = go.GetComponentsInChildren<Collider>();
            if (projCols != null && _player)
            {
                var ownerCols = _player.GetComponentsInChildren<Collider>(true);
                foreach (var pc in projCols)
                {
                    if (!pc) continue;
                    foreach (var oc in ownerCols)
                        if (oc) Physics.IgnoreCollision(pc, oc, true);
                }
            }

            var no = go.GetComponent<NetworkObject>();
            if (no) no.Spawn(true);

            var rb = go.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.velocity = velocity;
            }

            // Configure throwable if it has special script
            var throwable = go.GetComponent<ThrowableProjectile>();
            if (throwable)
            {
                var owner = _player ? _player : GetComponent<Game.Net.PlayerNetwork>();
                var ownerTeam = owner ? owner.GetTeam() : Game.Net.TeamId.A;
                throwable.ConfigureServer(OwnerClientId, ownerTeam, owner);
            }
        }

        Vector3 GetAimDir()
        {
            if (sockets && sockets.front)
            {
                var fwd = sockets.front.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.0001f) return fwd.normalized;
            }

            // Horizontal forward of the player
            var fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.right;
            return fwd.normalized;
        }

        Vector3 GetThrowDirection()
        {
            // Add upward angle to throw direction
            var horizontal = GetAimDir();
            var angleRad = throwAngle * Mathf.Deg2Rad;
            var direction = horizontal + Vector3.up * Mathf.Tan(angleRad);
            return direction.normalized;
        }

        Vector3 GetThrowOrigin()
        {
            if (sockets && sockets.utilityThrowOrigin)
                return sockets.utilityThrowOrigin.position;

            if (_view && _view.grip)
                return _view.grip.position;

            return transform.position + Vector3.up * 1.5f; // shoulder height fallback
        }

        bool TryGetThrowSolution(out Vector3 origin, out Vector3 velocity)
        {
            origin = GetThrowOrigin();
            var dir = GetThrowDirection();

            if (dir.sqrMagnitude < 1e-6f)
            {
                velocity = Vector3.zero;
                return false;
            }

            velocity = dir * throwForce;
            return true;
        }

        void LateUpdate()
        {
            if (!IsOwner)
            {
                HideArcIndicator();
                return;
            }

            if (_player && _player.GetActiveSlot() != Game.Net.WeaponSlot.Utility)
            {
                HideArcIndicator();
                return;
            }

            if (!_hasEquippedUtility || ammoCount.Value <= 0)
            {
                HideArcIndicator();
                return;
            }

            if (!TryGetThrowSolution(out var origin, out var velocity))
            {
                HideArcIndicator();
                return;
            }

            DrawArcIndicator(origin, velocity);
        }

        void DrawArcIndicator(Vector3 origin, Vector3 velocity)
        {
            if (!_arcRenderer)
            {
                var go = new GameObject("UtilityArc");
                go.transform.SetParent(transform, false);
                _arcRenderer = go.AddComponent<LineRenderer>();
                _arcRenderer.positionCount = k_ArcSegments;
                _arcRenderer.useWorldSpace = true;
                _arcRenderer.widthMultiplier = 0.05f;
                _arcRenderer.material = new Material(Shader.Find("Sprites/Default"));
                _arcRenderer.startColor = k_ArcColor;
                _arcRenderer.endColor = k_ArcColor;
                _arcRenderer.enabled = true;
            }

            var points = new Vector3[k_ArcSegments];
            float dt = k_PreviewDuration / Mathf.Max(1, k_ArcSegments - 1);
            var pos = origin;
            var vel = velocity;
            Vector3 landing = origin;

            for (int i = 0; i < k_ArcSegments; i++)
            {
                points[i] = pos;
                vel += Physics.gravity * dt;
                var next = pos + vel * dt;

                // Stop preview when we fall back to (or below) the spawn plane.
                if (next.y <= origin.y)
                {
                    landing = next;
                    points[i] = next;
                    for (int j = i + 1; j < k_ArcSegments; j++) points[j] = next;
                    break;
                }

                landing = next;
                pos = next;
            }

            _arcRenderer.SetPositions(points);
            _arcRenderer.enabled = true;
            ShowLandingMarker(landing);
        }

        void ShowLandingMarker(Vector3 position)
        {
            if (!_landingMarker)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "UtilityLanding";
                Destroy(go.GetComponent<Collider>());
                _landingMarker = go.transform;
                _landingMarker.SetParent(transform, false);
                _landingMarker.localScale = Vector3.one * 0.25f;
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer)
                {
                    renderer.material = new Material(Shader.Find("Standard"));
                    renderer.material.color = new Color(0.8f, 1f, 0.9f, 0.6f);
                }
            }

            _landingMarker.position = position;
            if (_landingMarker.gameObject.activeSelf == false)
                _landingMarker.gameObject.SetActive(true);
        }

        void HideArcIndicator()
        {
            if (_arcRenderer) _arcRenderer.enabled = false;
            if (_landingMarker) _landingMarker.gameObject.SetActive(false);
        }
    }
}
