using System.Collections.Generic;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering;
#endif

/// <summary>
/// Fades world geometry that occludes the line from camera → target.
/// Optionally fades the target renderers when occluded or very close to camera.
/// Client-only visual. No networking.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class LineOfSightOccluder : MonoBehaviour
{
[Header("Targets")]
public Transform target; // usually the local Player root
[Tooltip("If set, these renderers will be faded when occluded or too close.")]
public Renderer[] targetRenderersOverride;

[Header("Raycast")]
[Tooltip("Layers that can block line of sight to the target.")]
public LayerMask occluderLayers = ~0;
[Min(0f)] public float probeRadius = 0.25f;
[Min(1)]  public int maxHits = 12;
[Min(0.1f)] public float maxDistance = 200f;

[Header("Fading")]
[Range(0.05f, 1f)] public float occluderAlpha = 0.25f;
[Range(0.05f, 1f)] public float targetAlphaWhenOccluded = 0.6f;
[Min(0f)] public float fadeInSeconds = 0.15f;
[Min(0f)] public float fadeOutSeconds = 0.25f;

[Header("Proximity Fade")]
[Tooltip("Fade target when camera comes closer than this distance.")]
[Min(0f)] public float targetProximityMeters = 1.2f;

[Header("Options")]
public bool fadeTargetWhenOccluded = true;
public bool debugDraw;

Camera _cam;

struct Faded
{
    public Renderer r;
    public float fromA;
    public float toA;
    public float t;          // 0..1
    public float dur;        // seconds
    public bool active;      // true while under management
    public MaterialPropertyBlock mpb;
    public int colorId;
    public int baseColorId;
    public int tintId;
    public Color baseColor;
}

readonly Dictionary<Renderer, Faded> _current = new Dictionary<Renderer, Faded>(64);
readonly HashSet<Renderer> _thisFrame = new HashSet<Renderer>();
readonly List<RaycastHit> _hits = new List<RaycastHit>(16);

void Awake()
{
    _cam = GetComponent<Camera>();
}

void LateUpdate()
{
    if (!_cam || !target) return;

    // 1) Collect occluders along a spherecast from camera to target center.
    _thisFrame.Clear();
    var origin = _cam.transform.position;
    var dest = target.position;
    var dir = dest - origin;
    var dist = Mathf.Min(maxDistance, dir.magnitude);
    if (dist < 0.001f) return;
    dir /= dist;

    // Use SphereCast for stability with thin geometry.
    int count = Physics.SphereCastNonAlloc(origin, probeRadius, dir, GetBuffer(), dist, occluderLayers, QueryTriggerInteraction.Ignore);
    for (int i = 0; i < count && i < maxHits; i++)
    {
        var h = _hits[i];
        var rend = h.collider ? h.collider.GetComponentInParent<Renderer>() : null;
        if (!rend) continue;
        _thisFrame.Add(rend);
        FadeTo(rend, occluderAlpha, fadeOutSeconds);
        if (debugDraw) Debug.DrawLine(origin, h.point, Color.red);
    }

    // 2) Any previously faded renderer not hit this frame should restore.
    _RestoreMissing();

    // 3) Handle target transparency based on occlusion or proximity.
    if (fadeTargetWhenOccluded || targetProximityMeters > 0f)
    {
        bool occluded = _thisFrame.Count > 0;
        bool tooClose = Vector3.Distance(origin, dest) < targetProximityMeters;

        float want = (occluded || tooClose) ? targetAlphaWhenOccluded : 1f;
        var trs = ResolveTargetRenderers();
        for (int i = 0; i < trs.Length; i++)
            FadeTo(trs[i], want, want < 1f ? fadeOutSeconds : fadeInSeconds);
    }

    // 4) Advance fades and push MPBs.
    _TickFades();
}

// --- Internals ---
RaycastHit[] GetBuffer()
{
    // Keep a resizable backing list to avoid allocs
    if (_hits.Capacity < maxHits) _hits.Capacity = maxHits;
    if (_hits.Count < maxHits)
    {
        int need = maxHits - _hits.Count;
        for (int i = 0; i < need; i++) _hits.Add(default);
    }
    return _hits.ToArray(); // NonAlloc API requires array; this single ToArray is cached size-wise above
}

Renderer[] ResolveTargetRenderers()
{
    if (targetRenderersOverride != null && targetRenderersOverride.Length > 0)
        return targetRenderersOverride;

    // Try PlayerNetwork accessor to avoid GetComponents each frame.
    var pn = target.GetComponent<Game.Net.PlayerNetwork>();
    if (pn != null)
    {
        var span = pn.GetModelRenderersSpan();
        var result = new Renderer[span.Length];
        for (int i = 0; i < span.Length; i++) result[i] = span[i];
        return result;
    }
    // Fallback
    return target.GetComponentsInChildren<Renderer>(true);
}

void FadeTo(Renderer r, float targetA, float duration)
{
    if (!r) return;

    if (!_current.TryGetValue(r, out var f))
    {
        f = new Faded
        {
            r = r,
            mpb = new MaterialPropertyBlock(),
            colorId = Shader.PropertyToID("_Color"),
            baseColorId = Shader.PropertyToID("_BaseColor"),
            tintId = Shader.PropertyToID("_TintColor"),
            t = 0f,
            dur = Mathf.Max(0.0001f, duration),
            active = true
        };
        r.GetPropertyBlock(f.mpb);
        // Read any of the common color properties. Default to white if none exist.
        if (f.mpb.HasColor(f.baseColorId)) f.baseColor = f.mpb.GetColor(f.baseColorId);
        else if (f.mpb.HasColor(f.colorId)) f.baseColor = f.mpb.GetColor(f.colorId);
        else if (f.mpb.HasColor(f.tintId)) f.baseColor = f.mpb.GetColor(f.tintId);
        else f.baseColor = Color.white;
        f.fromA = f.baseColor.a;
        f.toA = targetA;
    }
    else
    {
        // Refresh target
        f.fromA = ReadCurrentAlpha(ref f);
        f.toA = targetA;
        f.t = 0f;
        f.dur = Mathf.Max(0.0001f, duration);
        f.active = true;
    }

    _current[r] = f;
}

float ReadCurrentAlpha(ref Faded f)
{
    r_GetBlock(ref f);
    if (f.mpb.HasColor(f.baseColorId)) return f.mpb.GetColor(f.baseColorId).a;
    if (f.mpb.HasColor(f.colorId)) return f.mpb.GetColor(f.colorId).a;
    if (f.mpb.HasColor(f.tintId)) return f.mpb.GetColor(f.tintId).a;
    return f.baseColor.a;
}

void _TickFades()
{
    var tmp = new List<Renderer>(_current.Keys);
    for (int i = 0; i < tmp.Count; i++)
    {
        var r = tmp[i];
        if (!_current.TryGetValue(r, out var f) || !f.active) continue;

        f.t += Time.unscaledDeltaTime / f.dur;
        float a = Mathf.Lerp(f.fromA, f.toA, Mathf.Clamp01(f.t));
        WriteAlpha(ref f, a);

        bool done = Mathf.Approximately(a, f.toA);
        if (done && Mathf.Approximately(a, 1f))
        {
            // Fully restored → stop managing
            _current.Remove(r);
            continue;
        }

        _current[r] = f;
    }
}

void _RestoreMissing()
{
    if (_thisFrame.Count == 0) return;
    var tmp = new List<Renderer>(_current.Keys);
    for (int i = 0; i < tmp.Count; i++)
    {
        var r = tmp[i];
        if (!_thisFrame.Contains(r) && _current.TryGetValue(r, out var f))
        {
            f.fromA = ReadCurrentAlpha(ref f);
            f.toA = 1f;
            f.t = 0f;
            f.dur = Mathf.Max(0.0001f, fadeInSeconds);
            _current[r] = f;
        }
    }
}

void WriteAlpha(ref Faded f, float a)
{
    r_GetBlock(ref f);
    var c = f.baseColor; c.a = a;

    // Try URP/HDRP "_BaseColor", then standard "_Color", then "_TintColor".
    if (f.mpb.HasColor(f.baseColorId)) f.mpb.SetColor(f.baseColorId, c);
    else if (f.mpb.HasColor(f.colorId)) f.mpb.SetColor(f.colorId, c);
    else f.mpb.SetColor(f.tintId, c);

    f.r.SetPropertyBlock(f.mpb);
}

static void r_GetBlock(ref Faded f)
{
    if (f.mpb == null) f.mpb = new MaterialPropertyBlock();
    f.r.GetPropertyBlock(f.mpb);
}


#if UNITY_EDITOR
void OnDrawGizmosSelected()
{
if (!debugDraw || !_cam || !target) return;
Gizmos.color = new Color(1, 0, 0, 0.25f);
var p0 = _cam.transform.position;
var p1 = target.position;
var dir = (p1 - p0);
var dist = dir.magnitude;
UnityEditor.Handles.DrawWireDisc(p0, _cam.transform.forward, probeRadius);
UnityEditor.Handles.DrawLine(p0, p1);
UnityEditor.Handles.DrawWireDisc(p1, _cam.transform.forward, probeRadius);
}
#endif
}
// Fades any renderer that blocks the camera→player line. Uses MPBs so it does not duplicate materials. Also fades the player when occluded or too close.