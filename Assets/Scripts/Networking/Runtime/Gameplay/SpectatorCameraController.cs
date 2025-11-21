// Assets/Scripts/Networking/Runtime/Gameplay/SpectatorCameraController.cs
// Client-only camera controller for spectator clients. Follows live players or loops cinematic waypoints when none are alive.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Net
{
    public static class SpectatorCameraBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var cam = Camera.main;
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (!cam) cam = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
#else
            if (!cam) cam = UnityEngine.Object.FindObjectOfType<Camera>();
#endif
            if (!cam) return;
            if (!cam.gameObject.GetComponent<SpectatorCameraController>())
                cam.gameObject.AddComponent<SpectatorCameraController>();
        }
    }

    [DisallowMultipleComponent]
    public sealed class SpectatorCameraController : MonoBehaviour
    {
        [Header("Targets")]
        [SerializeField] private Transform[] cinematicWaypoints;
        [SerializeField, Min(0.1f)] private float cinematicMoveSeconds = 6f;
        [SerializeField, Min(0f)] private float cinematicPauseSeconds = 0.5f;
        [SerializeField, Min(0.1f)] private float retargetIntervalSeconds = 0.5f;
        [SerializeField] private LayerMask occluderMask = ~0;

        private readonly List<PlayerNetwork> _candidates = new();
        private Transform _currentTarget;
        private Transform _cinematicFollower;
        private int _waypointIndex;
        private bool _waypointForward = true;
        private float _pauseUntil;
        private float _nextRefreshAt;

        private IsometricCamera _isoCam;
        private LineOfSightTransparency _los;
        private FovMesh _activeFov;

        void Awake()
        {
            BindCamera();
        }

        void OnEnable()
        {
            if (!ConnectionMetadata.LocalIsSpectator)
            {
                enabled = false;
                return;
            }

            EnsureCinematicFollower();
            RefreshCandidates(true);
            AutoChooseTarget();
        }

        void OnDisable()
        {
            SetFollowTarget(null);
        }

        void Update()
        {
            if (!ConnectionMetadata.LocalIsSpectator)
            {
                enabled = false;
                return;
            }

            HandleInput();

            if (Time.unscaledTime >= _nextRefreshAt)
            {
                RefreshCandidates();
                _nextRefreshAt = Time.unscaledTime + retargetIntervalSeconds;
            }

            if (!IsValidTarget(_currentTarget))
                AutoChooseTarget();

            if (!IsValidTarget(_currentTarget))
            {
                AdvanceCinematic();
            }
        }

        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.qKey.wasPressedThisFrame) Cycle(-1);
            if (kb.eKey.wasPressedThisFrame) Cycle(1);
        }

        void Cycle(int direction)
        {
            if (_candidates.Count == 0) { _currentTarget = null; return; }

            int currentIndex = -1;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i] && _candidates[i].transform == _currentTarget)
                {
                    currentIndex = i;
                    break;
                }
            }

            int nextIndex = (currentIndex + direction) % _candidates.Count;
            if (nextIndex < 0) nextIndex = _candidates.Count - 1;
            SetFollowTarget(_candidates[nextIndex] ? _candidates[nextIndex].transform : null);
        }

        void RefreshCandidates(bool force = false)
        {
            _candidates.Clear();
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            var players = FindObjectsOfType<PlayerNetwork>();
#endif
            foreach (var pn in players)
            {
                if (IsTargetable(pn))
                    _candidates.Add(pn);
            }

            if (force && _candidates.Count > 0)
                SetFollowTarget(_candidates[0].transform);
        }

        void AutoChooseTarget()
        {
            if (_candidates.Count > 0)
            {
                SetFollowTarget(_candidates[0].transform);
            }
            else
            {
                SetFollowTarget(null);
            }
        }

        bool IsTargetable(PlayerNetwork pn)
        {
            if (!pn || !pn.IsSpawned) return false;
            return pn.GetHealth() > 0.01f;
        }

        static bool IsValidTarget(Transform t)
        {
            return t && t.gameObject.activeInHierarchy;
        }

        void AdvanceCinematic()
        {
            EnsureCinematicFollower();
            if (_cinematicFollower == null || cinematicWaypoints == null || cinematicWaypoints.Length == 0)
                return;

            if (!IsValidTarget(_currentTarget))
                SetFollowTarget(_cinematicFollower);

            if (Time.unscaledTime < _pauseUntil) return;

            var target = cinematicWaypoints[_waypointIndex];
            if (!target)
            {
                NextWaypoint();
                return;
            }

            float t = cinematicMoveSeconds <= 0f ? 1f : Time.unscaledDeltaTime / cinematicMoveSeconds;
            _cinematicFollower.position = Vector3.Lerp(_cinematicFollower.position, target.position, t);
            _cinematicFollower.rotation = Quaternion.Slerp(_cinematicFollower.rotation, target.rotation, t);

            if (Vector3.SqrMagnitude(_cinematicFollower.position - target.position) < 0.05f)
            {
                _pauseUntil = Time.unscaledTime + cinematicPauseSeconds;
                NextWaypoint();
            }
        }

        void NextWaypoint()
        {
            if (cinematicWaypoints == null || cinematicWaypoints.Length == 0) return;

            if (_waypointForward)
            {
                _waypointIndex++;
                if (_waypointIndex >= cinematicWaypoints.Length)
                {
                    _waypointIndex = cinematicWaypoints.Length - 2;
                    _waypointForward = false;
                }
            }
            else
            {
                _waypointIndex--;
                if (_waypointIndex < 0)
                {
                    _waypointIndex = 1;
                    _waypointForward = true;
                }
            }
            _waypointIndex = Mathf.Clamp(_waypointIndex, 0, Mathf.Max(0, cinematicWaypoints.Length - 1));
        }

        void SetFollowTarget(Transform target)
        {
            EnsureCamera();
            _currentTarget = target;

            UpdateTargetFov(target);

            var follow = target;
            if (!follow)
            {
                EnsureCinematicFollower();
                follow = _cinematicFollower;
            }

            if (_isoCam) _isoCam.follow = follow;
            if (_los)
            {
                _los.target = follow;
                _los.occluderLayers = occluderMask;
            }
        }

        void EnsureCinematicFollower()
        {
            if (_cinematicFollower != null) return;
            var go = new GameObject("SpectatorCinematicFollower");
            _cinematicFollower = go.transform;
        }

        void BindCamera()
        {
            EnsureCamera();
            if (_isoCam)
            {
                _los = _isoCam.GetComponent<LineOfSightTransparency>() ?? _isoCam.gameObject.AddComponent<LineOfSightTransparency>();
                _los.target = _isoCam.follow;
                _los.occluderLayers = occluderMask;
            }
        }

        void EnsureCamera()
        {
            if (_isoCam != null) return;
            var cam = Camera.main;
            if (!cam)
            {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
                cam = FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
#else
                cam = FindObjectOfType<Camera>();
#endif
            }
            if (!cam) return;
            _isoCam = cam.GetComponent<IsometricCamera>() ?? cam.gameObject.AddComponent<IsometricCamera>();
        }

        void UpdateTargetFov(Transform target)
        {
            var pn = target ? target.GetComponentInParent<PlayerNetwork>() : null;

            if (_activeFov && pn && _activeFov.transform == pn.transform)
                return;

            if (_activeFov)
            {
                _activeFov.enabled = false;
                _activeFov.showVisual = false;
                _activeFov = null;
            }

            if (!pn || !pn.IsSpawned || pn.GetHealth() <= 0.01f) return;

            var fov = pn.GetComponent<FovMesh>();
            if (!fov) fov = pn.gameObject.AddComponent<FovMesh>();

            fov.radiusMeters = 12f;
            fov.rayCount = 220;
            fov.occluderMask = LayerMask.GetMask("Occluder", "OccluderExtra");
            fov.showFill = true;
            fov.fillColor = new Color(0.95f, 0.97f, 1.0f, 0.45f);
            fov.fillIntensity = 1.15f;
            fov.edgeFeather = 0.15f;
            fov.showVisual = true;
            fov.visualColor = new Color(0.92f, 0.97f, 1.0f, 0.62f);
            fov.follow = pn.transform;
            fov.enabled = true;

            var constraint = pn.GetComponent<FovVisualConstraintBinder>() ?? pn.gameObject.AddComponent<FovVisualConstraintBinder>();
            constraint.Source = pn.transform;

            _activeFov = fov;
        }
    }
}
