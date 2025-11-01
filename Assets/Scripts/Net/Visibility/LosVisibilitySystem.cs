using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Game.Net;

/// <summary>
/// Server-only LOS manager. Per-client observer filtering:
/// - Reveals enemies within radius and not occluded by Environment.
/// - Sends a client-targeted fade-out RPC before hiding, then NetworkHide after fade.
/// Assumes flat map; casts at eye height.
/// </summary>
public sealed class LosVisibilitySystem : MonoBehaviour
{
    public static LosVisibilitySystem Instance { get; private set; }

    struct Tracked
    {
        public NetworkObject NO;
        public Game.Net.TeamId Team;
        public Transform Transform;
        public ulong OwnerId;
    }

    NetworkManager _nm;
    float _radius;
    float _fadeSec;
    int _occluderMask;
    float _eyeHeight = 1.2f;
    float _tick = 0.066f; // ~15 Hz

    // visibility[viewerId][targetNetId] = visible?
    readonly Dictionary<ulong, Dictionary<ulong, bool>> _visibility = new();
    // targets by netId
    readonly Dictionary<ulong, Tracked> _targets = new();

    public static void Install(NetworkManager networkManager, float losRadiusMeters, float fadeSeconds, int occluderMask)
    {
        // ensure this file already uses both layers per your last request
        if (Instance != null) return;
        var go = new GameObject("LosVisibilitySystem");
        DontDestroyOnLoad(go);
        var sys = go.AddComponent<LosVisibilitySystem>();
        sys._nm = networkManager;
        sys._radius = Mathf.Max(0.1f, losRadiusMeters);
        sys._fadeSec = Mathf.Max(0.05f, fadeSeconds);
        sys._occluderMask = occluderMask;
        Instance = sys;
        sys.StartCoroutine(sys.CoLoop());
        networkManager.OnClientConnectedCallback  += sys.OnClientConnected;
        networkManager.OnClientDisconnectCallback += sys.OnClientDisconnected;
    }

    public static void Shutdown()
    {
        if (Instance == null) return;
        if (Instance._nm) 
        {
            Instance._nm.OnClientConnectedCallback  -= Instance.OnClientConnected;
            Instance._nm.OnClientDisconnectCallback -= Instance.OnClientDisconnected;
        }
        Destroy(Instance.gameObject);
        Instance = null;
    }

    public void RegisterTarget(NetworkObject no, Game.Net.TeamId team)
    {
        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsServer) return;
        var key = no.NetworkObjectId;
        if (_targets.ContainsKey(key)) return;
        var tracked = new Tracked
        {
            NO = no,
            Team = team,
            Transform = no.transform,
            OwnerId = no.OwnerClientId
        };
        _targets[key] = tracked;

        // Immediate per-viewer visibility on spawn to avoid one-frame leaks.
        var viewers = _nm.ConnectedClientsIds;
        foreach (var viewerId in viewers)
            ApplyImmediateVisibilityForViewer(viewerId, tracked);
    }

    public void UnregisterTarget(NetworkObject no)
    {
        if (!no) return;
        _targets.Remove(no.NetworkObjectId);
        foreach (var kv in _visibility.Values)
            kv.Remove(no.NetworkObjectId);
    }

    void OnClientDisconnected(ulong clientId)
    {
        _visibility.Remove(clientId);
    }

    void OnClientConnected(ulong viewerId)
    {
        // Build map
        if (!_visibility.ContainsKey(viewerId))
            _visibility[viewerId] = new Dictionary<ulong, bool>();

        // Enforce initial state for all existing targets for this viewer.
        foreach (var t in _targets.Values)
            ApplyImmediateVisibilityForViewer(viewerId, t);
    }

    IEnumerator CoLoop()
    {
        var wait = new WaitForSeconds(_tick);
        while (true)
        {
            if (_nm && _nm.IsServer)
                Tick();
            yield return wait;
        }
    }

    void Tick()
    {
        // Build viewers set
        var viewers = _nm.ConnectedClientsIds;
        foreach (var viewerId in viewers)
        {
            // Ensure map
            if (!_visibility.TryGetValue(viewerId, out var map))
            {
                map = new Dictionary<ulong, bool>();
                _visibility[viewerId] = map;
            }

            // Find viewer's team from tracked NO
            Game.Net.TeamId viewerTeam = Game.Net.TeamId.A;
            Transform viewerTf = null;
            {
                if (_nm.ConnectedClients.TryGetValue(viewerId, out var viewerClient) && viewerClient.PlayerObject)
                {
                    var vno = viewerClient.PlayerObject;
                    if (_targets.TryGetValue(vno.NetworkObjectId, out var vt))
                    {
                        viewerTeam = vt.Team;
                        viewerTf = vt.Transform;
                    }
                }
            }
            if (!viewerTf) continue;

            var viewerEye = viewerTf.position + Vector3.up * _eyeHeight;

            // Iterate opponents
            foreach (var t in _targets.Values)
            {
                if (t.OwnerId == viewerId) continue;         // skip self
                var targetId = t.NO.NetworkObjectId;
                if (t.Team == viewerTeam) {                   // allies always visible
                    // Ensure ally is shown if a previous round hid it for this viewer.
                    if (!t.NO.IsNetworkVisibleTo(viewerId)) t.NO.NetworkShow(viewerId);
                    map[targetId] = true;
                    continue;
                }

                // Compute desired visibility
                bool wantVisible = false;
                var to = t.Transform.position - viewerTf.position;
                if (to.sqrMagnitude <= _radius * _radius)
                {
                    var targetEye = t.Transform.position + Vector3.up * _eyeHeight;
                    // Ignore viewer/target self-colliders when checking occlusion.
                    if (!HasOccluderBetween(viewerEye, targetEye, viewerTf, t.Transform))
                        wantVisible = true;
                }

                map.TryGetValue(targetId, out var curVisible);
                if (wantVisible == curVisible) continue;

                if (wantVisible)
                {
                    if (!t.NO.IsNetworkVisibleTo(viewerId))
                        t.NO.NetworkShow(viewerId);
                    // Cancel any residual fade on reveal for this viewer.
                    var fader = t.NO.GetComponent<LosFader>();
                    if (fader != null) fader.CancelFade();
                    map[targetId] = true;
                }
                else
                {
                    var fader = t.NO.GetComponent<LosFader>();
                    if (fader != null)
                        fader.RequestFadeOutTargeted(viewerId, _fadeSec);

                    StartCoroutine(CoHideAfter(t.NO, viewerId, _fadeSec + 0.05f));
                    map[targetId] = false;
                }
            }
        }
    }

    IEnumerator CoHideAfter(NetworkObject no, ulong viewerId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (no && no.IsSpawned && no.IsNetworkVisibleTo(viewerId))
            no.NetworkHide(viewerId);
    }

    bool HasOccluderBetween(Vector3 a, Vector3 b, Transform viewer, Transform target)
    {
        var dir = b - a;
        float dist = dir.magnitude;
        if (dist <= 0.001f) return false;
        dir /= dist;

        var hits = Physics.RaycastAll(a, dir, dist, _occluderMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (!h.collider) continue;
            var t = h.collider.transform;
            if (viewer && t.IsChildOf(viewer)) continue;
            if (target && t.IsChildOf(target)) continue;
            return true; // real occluder hit
        }
        return false;
    }

    void ApplyImmediateVisibilityForViewer(ulong viewerId, Tracked t)
    {
        if (!_nm || !_nm.IsServer) return;

        // Self is always visible.
        if (viewerId == t.OwnerId)
        {
            if (!t.NO.IsNetworkVisibleTo(viewerId)) t.NO.NetworkShow(viewerId);
            SetMapVisible(viewerId, t.NO.NetworkObjectId, true);
            return;
        }

        // Allies always visible.
        var viewerTeam = GetViewerTeam(viewerId);
        if (viewerTeam == t.Team)
        {
            if (!t.NO.IsNetworkVisibleTo(viewerId)) t.NO.NetworkShow(viewerId);
            SetMapVisible(viewerId, t.NO.NetworkObjectId, true);
            return;
        }

        // LOS check at install time.
        bool visible = IsVisible(viewerId, t);
        if (visible)
        {
            if (!t.NO.IsNetworkVisibleTo(viewerId)) t.NO.NetworkShow(viewerId);
            // cancel any residual fade
            var f = t.NO.GetComponent<LosFader>(); if (f) f.CancelFade();
            SetMapVisible(viewerId, t.NO.NetworkObjectId, true);
        }
        else
        {
            if (t.NO.IsNetworkVisibleTo(viewerId)) t.NO.NetworkHide(viewerId); // immediate hide, no fade on first frame
            SetMapVisible(viewerId, t.NO.NetworkObjectId, false);
        }
    }

    TeamId GetViewerTeam(ulong viewerId)
    {
        if (_nm.ConnectedClients.TryGetValue(viewerId, out var vc) && vc.PlayerObject)
        {
            if (_targets.TryGetValue(vc.PlayerObject.NetworkObjectId, out var tr))
                return tr.Team;
        }
        return TeamId.A; // default
    }

    bool IsVisible(ulong viewerId, Tracked t)
    {
        // Resolve viewer transform
        Transform viewerTf = null;
        if (_nm.ConnectedClients.TryGetValue(viewerId, out var v) && v.PlayerObject)
        {
            if (_targets.TryGetValue(v.PlayerObject.NetworkObjectId, out var vt))
                viewerTf = vt.Transform;
        }
        if (!viewerTf) return false;

        var viewerEye = viewerTf.position + Vector3.up * _eyeHeight;
        var targetEye = t.Transform.position + Vector3.up * _eyeHeight;

        if ((targetEye - viewerEye).sqrMagnitude > _radius * _radius) return false;
        return !HasOccluderBetween(viewerEye, targetEye, viewerTf, t.Transform);
    }

    void SetMapVisible(ulong viewerId, ulong targetId, bool visible)
    {
        if (!_visibility.TryGetValue(viewerId, out var map))
        {
            map = new Dictionary<ulong, bool>();
            _visibility[viewerId] = map;
        }
        map[targetId] = visible;
    }
}