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
    [Tooltip("Do not run outside Play Mode. Restores all fades when disabled.")]
    public bool playModeOnly = true;

    [Tooltip("If true, try GetComponentInParent<Renderer>(). If false, require Renderer on the hit collider GO.")]
    public bool allowParentRendererLookup = false;

    [Tooltip("Do not let occluders go below this alpha.")]
    [Range(0.1f, 1f)] public float minOccluderAlpha = 0.30f;

    [Header("Mode")]
    public bool useCircularCutout = true;     // enable per-pixel circle instead of whole-mesh fade

    [Header("Cutout (viewport units 0..1)")]
    [Range(0.02f, 0.5f)] public float cutoutRadius = 0.12f;
    [Range(0.005f, 0.3f)] public float cutoutFeather = 0.06f; // soft edge
    [Range(0.1f, 1f)] public float cutoutAlpha = 0.30f;       // 0.30 = ~70% transparent inside circle
    [Min(0f)] public float cutoutReleaseDelay = 0.1f;

    // Shader property ids
    static readonly int _LosCenterId = Shader.PropertyToID("_LosCenter");
    static readonly int _LosRadiusId = Shader.PropertyToID("_LosRadius");
    static readonly int _LosFeatherId = Shader.PropertyToID("_LosFeather");
    static readonly int _CutAlphaId  = Shader.PropertyToID("_CutAlpha");
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

    // Pooled raycast buffer to avoid per-frame allocations.
    RaycastHit[] _hitsArr;

struct Faded
{
    public Renderer r;
    public float fromA;
    public float toA;
    public float t;          // 0..1
    public float dur;        // seconds
    public bool active;      // true while under management
    public bool isCutout;    // true when managed via per-pixel cutout
    public float holdUntil;  // timestamp to keep cutout alive when not hit this frame
    public MaterialPropertyBlock mpb;
    public int colorId;
    public int baseColorId;
    public int tintId;
    public Color baseColor;
}

readonly Dictionary<Renderer, Faded> _current = new Dictionary<Renderer, Faded>(64);
readonly HashSet<Renderer> _thisFrame = new HashSet<Renderer>();

void Awake()
{
    _cam = GetComponent<Camera>();
    if (_hitsArr == null || _hitsArr.Length != Mathf.Max(8, maxHits))
        _hitsArr = new RaycastHit[Mathf.Max(8, maxHits)];

    Debug.Log($"[LOS] mask={occluderLayers.value} probe={probeRadius} maxHits={maxHits}");
}

void OnDisable() => _RestoreAllToOpaque();
void OnDestroy() => _RestoreAllToOpaque();

void _RestoreAllToOpaque()
{
    if (_current.Count == 0) return;
    var tmp = new List<Renderer>(_current.Keys);
    for (int i = 0; i < tmp.Count; i++)
    {
        var r = tmp[i];
        if (!_current.TryGetValue(r, out var f)) continue;

        if (useCircularCutout)
        {
            if (f.mpb == null) f.mpb = new MaterialPropertyBlock();
            f.mpb.SetFloat(_LosRadiusId, 0f);
            f.mpb.SetFloat(_LosFeatherId, 0f);
            f.mpb.SetFloat(_CutAlphaId, 1f);
            r.SetPropertyBlock(f.mpb);
            f.isCutout = false;
        }
        else
        {
            f.t = 1f; f.toA = 1f; WriteAlpha(ref f, 1f);
        }
    }
    _current.Clear();
}

// Prevents SceneView artifacts and restores after Play Mode stops.

void LateUpdate()
{
    if (playModeOnly && !Application.isPlaying)
    {
        _RestoreAllToOpaque();
        return;
    }

    if (!_cam || !target) return;

    // 0) Compute screen-space center for cutout
    var vp = _cam.WorldToViewportPoint(target.position);
    var vpCenter = new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));

    // 1) Collect occluders strictly between camera and player.
    _thisFrame.Clear();
    var origin = _cam.transform.position;
    var dest = target.position;
    var dir = dest - origin;
    var dist = Mathf.Min(maxDistance, dir.magnitude);
    if (dist < 0.001f) return;
    dir /= dist;

    if (useCircularCutout)
    {
        CollectCutoutOccluders(origin, dist, vpCenter);
    }
    else
    {
        // Use SphereCast for stability with thin geometry.
        // Ensure buffer capacity if inspector value changed at runtime.
        if (_hitsArr == null || _hitsArr.Length < maxHits) _hitsArr = new RaycastHit[Mathf.Max(8, maxHits)];

        int count = Physics.SphereCastNonAlloc(origin, probeRadius, dir, _hitsArr, dist, occluderLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count && i < maxHits; i++)
        {
            var h = _hitsArr[i];
            if (!h.collider) continue;
            if (target && h.collider.transform.IsChildOf(target)) continue;

            // Narrow: prefer Renderer on the hit GO. Optional parent lookup.
            var rend = h.collider.GetComponent<Renderer>();
            if (!rend && allowParentRendererLookup) rend = h.collider.GetComponentInParent<Renderer>();
            if (!rend) continue;

            if (!_thisFrame.Add(rend)) continue;

            var targetA = Mathf.Max(minOccluderAlpha, occluderAlpha);
            FadeTo(rend, targetA, fadeOutSeconds);

            if (debugDraw) Debug.DrawLine(origin, h.point, Color.red);
        }
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
void ApplyCutout(Renderer r, Vector2 vpCenter, float radius, float feather, float alphaInside)
{
    if (!_current.TryGetValue(r, out var f))
    {
        f = new Faded { r = r, mpb = new MaterialPropertyBlock(), active = false, isCutout = true };
    }
    else
    {
        f.active = false;
        f.isCutout = true;
    }
    if (f.mpb == null) f.mpb = new MaterialPropertyBlock();

    r.GetPropertyBlock(f.mpb);
    f.mpb.SetVector(_LosCenterId, new Vector4(vpCenter.x, vpCenter.y, 0, 0));
    f.mpb.SetFloat(_LosRadiusId, radius);
    f.mpb.SetFloat(_LosFeatherId, feather);
    f.mpb.SetFloat(_CutAlphaId, Mathf.Clamp01(alphaInside));
    r.SetPropertyBlock(f.mpb);

    // Ensure the fade system keeps this renderer opaque outside the cutout.
    f.fromA = 1f;
    f.toA = 1f;
    f.t = 0f;
    f.dur = 0f;
    f.holdUntil = Time.unscaledTime + cutoutReleaseDelay;

    _current[r] = f;
}

void CollectCutoutOccluders(Vector3 origin, float dist, Vector2 vpCenter)
{
    // Center ray directly toward target
    SampleCutoutRay(vpCenter, dist, vpCenter);

    // Radial samples around the cutout circle to catch edges
    const int ringSamples = 6;
    float sampleRadius = Mathf.Min(0.95f, cutoutRadius * 0.75f);
    for (int i = 0; i < ringSamples; i++)
    {
        float angle = (Mathf.PI * 2f * i) / ringSamples;
        var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * sampleRadius;
        var sample = vpCenter + offset;
        sample.x = Mathf.Clamp01(sample.x);
        sample.y = Mathf.Clamp01(sample.y);
        SampleCutoutRay(sample, dist, vpCenter);
    }
}

void SampleCutoutRay(Vector2 sample, float dist, Vector2 vpCenter)
{
    var ray = _cam.ViewportPointToRay(new Vector3(sample.x, sample.y, 0f));
    if (!Physics.Raycast(ray, out var hit, dist, occluderLayers, QueryTriggerInteraction.Ignore)) return;
    if (!hit.collider || (target && hit.collider.transform.IsChildOf(target))) return;
    if (hit.distance > dist + 0.01f) return;

    var rend = hit.collider.GetComponent<Renderer>();
    if (!rend && allowParentRendererLookup) rend = hit.collider.GetComponentInParent<Renderer>();
    if (!rend) return;

    _thisFrame.Add(rend);
    ApplyCutout(rend, vpCenter, cutoutRadius, cutoutFeather, cutoutAlpha);

    if (debugDraw) Debug.DrawLine(ray.origin, hit.point, Color.cyan);
}

// Adds a screen-space circular cutout path. Whole-mesh fade remains available via useCircularCutout=false.

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
            active = true,
            isCutout = false,
            holdUntil = 0f
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
        f.isCutout = false;
        f.holdUntil = 0f;
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
    // Safety clamp for occluders each tick.
    // If a fade is heading to < minOccluderAlpha and it's an occluder (not the target), keep it clamped.
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
    var tmp = new List<Renderer>(_current.Keys);
    for (int i = 0; i < tmp.Count; i++)
    {
        var r = tmp[i];
        if (_thisFrame.Contains(r)) continue;

        if (useCircularCutout)
        {
            // Clear cutout on renderers not hit this frame
            var f = _current[r];
            if (f.isCutout && Time.unscaledTime < f.holdUntil)
            {
                _current[r] = f;
                continue;
            }
            if (f.mpb == null) f.mpb = new MaterialPropertyBlock();
            f.mpb.SetFloat(_LosRadiusId, 0f);
            f.mpb.SetFloat(_LosFeatherId, 0f);
            f.mpb.SetFloat(_CutAlphaId, 1f);
            r.SetPropertyBlock(f.mpb);
            _current.Remove(r);
        }
        else if (_current.TryGetValue(r, out var f2))
        {
            f2.fromA = ReadCurrentAlpha(ref f2);
            f2.toA = 1f; f2.t = 0f; f2.dur = Mathf.Max(0.0001f, fadeInSeconds);
            _current[r] = f2;
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