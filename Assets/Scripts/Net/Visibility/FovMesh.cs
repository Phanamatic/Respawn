using System.Collections.Generic;
using UnityEngine;
using Game.Net;

/// <summary>
/// Builds a 360° field-of-view mesh around the local player by raycasting against Occluder layer.
/// Renders an invisible mesh that writes stencil; the overlay darkens everything outside it.
/// Flat map assumption: XZ plane; eye height ~1.2m.
/// </summary>
[RequireComponent(typeof(Transform))]
public sealed class FovMesh : MonoBehaviour
{
    [Range(0.5f, 50f)] public float radiusMeters = 12f;
    [Range(16, 512)] public int rayCount = 220;
    public LayerMask occluderMask;
    [Tooltip("Optional anchor for the FOV center (use the visual root).")]
    public Transform follow;
    public bool showFill = true;
    public Color fillColor = new Color(0.95f, 0.97f, 1.0f, 0.45f);
    [Range(0.1f, 3f)] public float fillIntensity = 1.15f;
    [Range(0f, 0.5f)] public float edgeFeather = 0.15f;

    [Header("Visual Geometry")]
    public bool showVisual = true;
    public Color visualColor = new Color(0.92f, 0.97f, 1.0f, 0.62f);
    [Range(0.02f, 1f)] public float visualEdgeSoftness = 0.35f;
    [Range(0f, 0.2f)] public float visualHeightOffset = 0.03f;

    const float EyeHeight = 1.2f;
    const float RebuildHz = 12f;

    static readonly int _ShowFillId  = Shader.PropertyToID("_ShowFill");
    static readonly int _FillMulId   = Shader.PropertyToID("_FillMul");
    static readonly int _FeatherId   = Shader.PropertyToID("_Feather");
    static readonly int _RadiusId    = Shader.PropertyToID("_Radius");
    static readonly int _VisualTintId = Shader.PropertyToID("_Tint");
    static readonly int _VisualEdgeId = Shader.PropertyToID("_EdgeFade");

    Mesh _mesh;
    GameObject _meshGO;
    MeshFilter _mf;
    MeshRenderer _mr;
    GameObject _visualGO;
    MeshRenderer _visualRenderer;
    float _accum;
    static Material _stencilMat;
    static Material _visualMat;
    MaterialPropertyBlock _mrMpb;
    MaterialPropertyBlock _visualMpb;
    readonly List<Vector3> _verts = new List<Vector3>(256);
    readonly List<int> _tris = new List<int>(768);

    void OnEnable()
    {
        if (!_mesh) _mesh = new Mesh { name = "FOV_Mesh" };

        // Create a dedicated child renderer so we never touch the player's own MeshRenderer.
        if (_meshGO == null)
        {
            _meshGO = new GameObject("__FOVMesh");
            _meshGO.transform.SetParent(transform, worldPositionStays: false);
            _meshGO.transform.localPosition = Vector3.zero;
            _meshGO.transform.localRotation = Quaternion.identity;
            _meshGO.transform.localScale = Vector3.one;

            // Render on the same dedicated layer the overlay will use.
            int fovLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (fovLayer < 0) fovLayer = 0; // Default fallback
            _meshGO.layer = fovLayer;

            _mf = _meshGO.AddComponent<MeshFilter>();
            _mr = _meshGO.AddComponent<MeshRenderer>();
        }

        // Auto-assign follow if missing.
        if (!follow)
        {
            var pn = GetComponent<PlayerNetwork>();
            if (pn && pn.transform) follow = pn.transform;
        }

        _mf.sharedMesh = _mesh;

        if (_stencilMat == null)
        {
            _stencilMat = Resources.Load<Material>("LOS/FOVStencilWriteMat");
            if (_stencilMat == null)
            {
                var sh = Shader.Find("Custom/LOS/FOVStencilWrite");
                if (sh) _stencilMat = new Material(sh) { name = "FOVStencilWriteMat(Runtime)" };
            }
        }
        _mr.sharedMaterial = _stencilMat;

        _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _mr.receiveShadows = false;
#if UNITY_6000_0_OR_NEWER
        _mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
#else
        _mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
#endif
        _mr.allowOcclusionWhenDynamic = false;
        _mr.enabled = (_mr.sharedMaterial != null);
        // Belt-and-braces: the FOV mesh should also never participate in lighting.
        _mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        _mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

        if (showVisual) EnsureVisualRenderer();
        else DisableVisualRenderer();
    }

    void OnDisable()
    {
        if (_mf) _mf.sharedMesh = null;
        if (_meshGO) _meshGO.SetActive(false);
        DisableVisualRenderer();
    }

    private void Update()
    {
        if (follow != null)
        {
            transform.position = follow.position;
        }
        if (transform.rotation != Quaternion.identity)
            transform.rotation = Quaternion.identity;

        // update fill properties on material
        if (_mr != null)
        {
            if (_mrMpb == null) _mrMpb = new MaterialPropertyBlock();
            _mr.GetPropertyBlock(_mrMpb);
            _mrMpb.Clear();
            _mrMpb.SetFloat(_ShowFillId, showFill ? 1.0f : 0.0f);
            _mrMpb.SetColor("_FillColor", fillColor);
            _mrMpb.SetFloat(_FillMulId, Mathf.Max(0.1f, fillIntensity));
            _mrMpb.SetFloat(_FeatherId, Mathf.Max(0f, edgeFeather));
            _mrMpb.SetFloat(_RadiusId, Mathf.Max(0.1f, radiusMeters));
            _mr.SetPropertyBlock(_mrMpb);
        }

        if (_meshGO && !_meshGO.activeSelf) _meshGO.SetActive(true);

        if (showVisual)
        {
            if (_visualRenderer == null) EnsureVisualRenderer();
            if (_visualRenderer != null)
            {
                if (_visualGO && !_visualGO.activeSelf) _visualGO.SetActive(true);
                if (_visualMpb == null) _visualMpb = new MaterialPropertyBlock();
                _visualRenderer.GetPropertyBlock(_visualMpb);
                _visualMpb.Clear();
                _visualMpb.SetColor(_VisualTintId, visualColor);
                _visualMpb.SetFloat(_VisualEdgeId, Mathf.Max(0.02f, visualEdgeSoftness));
                _visualMpb.SetFloat(_RadiusId, Mathf.Max(0.1f, radiusMeters));
                _visualRenderer.SetPropertyBlock(_visualMpb);
                if (_visualGO)
                    _visualGO.transform.localPosition = new Vector3(0f, visualHeightOffset, 0f);
            }
        }
        else if (_visualGO && _visualGO.activeSelf)
        {
            _visualGO.SetActive(false);
        }

        _accum += Time.unscaledDeltaTime;
        if (_accum >= 1f / RebuildHz)
        {
            _accum = 0f;
            RebuildMesh();
        }
    }

    void RebuildMesh()
    {
        if (_mesh == null) return;

        // World-space probe for occlusion.
        var centerWS = follow ? follow.position : transform.position;
        var eyeWS = centerWS + Vector3.up * EyeHeight;

        int n = Mathf.Clamp(rayCount, 16, 220);
    _verts.Clear();
    _tris.Clear();
    if (_verts.Capacity < n + 1) _verts.Capacity = n + 1;
    if (_tris.Capacity < n * 3) _tris.Capacity = n * 3;

        float step = 360f / n;

        // Mesh vertices must be in LOCAL space.
    _verts.Add(new Vector3(0f, 0.05f, 0f)); // center at player origin

        for (int i = 0; i < n; i++)
        {
            float ang = step * i * Mathf.Deg2Rad;
            var dirWS = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            Vector3 endWS = centerWS + dirWS * radiusMeters;

            if (Physics.Linecast(eyeWS, endWS + Vector3.up * EyeHeight, out var hit, occluderMask, QueryTriggerInteraction.Ignore))
            {
                endWS = hit.point;
            }

            // Keep the ring on the ground plane near the player pivot.
            var endOnPlaneWS = new Vector3(endWS.x, centerWS.y + 0.05f, endWS.z);
            var endLS = transform.InverseTransformPoint(endOnPlaneWS);
            _verts.Add(endLS);
        }

        for (int i = 0; i < n - 1; i++)
        {
            _tris.Add(0); _tris.Add(i + 1); _tris.Add(i + 2);
        }
        _tris.Add(0); _tris.Add(n); _tris.Add(1);

        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetTriangles(_tris, 0, true);
        _mesh.RecalculateBounds();

        if (_visualRenderer)
        {
            var mf = _visualGO ? _visualGO.GetComponent<MeshFilter>() : null;
            if (mf && mf.sharedMesh != _mesh) mf.sharedMesh = _mesh;
        }
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (!c) c = go.AddComponent<T>();
        return c;
    }

    void EnsureVisualRenderer()
    {
        if (_visualGO == null)
        {
            _visualGO = new GameObject("__FOVVisual");
            _visualGO.transform.SetParent(transform, worldPositionStays: false);
            _visualGO.transform.localRotation = Quaternion.identity;
            _visualGO.transform.localScale = Vector3.one;
        }

        int fovLayer = LayerMask.NameToLayer("Ignore Raycast");
        if (fovLayer < 0) fovLayer = gameObject.layer;
        _visualGO.layer = fovLayer;

        var mf = GetOrAdd<MeshFilter>(_visualGO);
        mf.sharedMesh = _mesh;

        _visualRenderer = GetOrAdd<MeshRenderer>(_visualGO);
        if (_visualMat == null)
        {
            _visualMat = Resources.Load<Material>("LOS/FOVVisualMat");
            if (_visualMat == null)
            {
                var sh = Shader.Find("Custom/LOS/FOVVisual");
                if (sh) _visualMat = new Material(sh) { name = "FOVVisualMat(Runtime)" };
            }
        }

        if (_visualMat != null) _visualRenderer.sharedMaterial = _visualMat;
        _visualRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _visualRenderer.receiveShadows = false;
#if UNITY_6000_0_OR_NEWER
        _visualRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
#else
        _visualRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
#endif
        _visualRenderer.allowOcclusionWhenDynamic = false;
        _visualRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        _visualRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        _visualRenderer.enabled = (_visualRenderer.sharedMaterial != null);
        if (_visualGO && !_visualGO.activeSelf) _visualGO.SetActive(_visualRenderer.enabled);
        _visualGO.transform.localPosition = new Vector3(0f, visualHeightOffset, 0f);
    }

    void DisableVisualRenderer()
    {
        if (_visualGO) _visualGO.SetActive(false);
    }
}
// Keeps the stencil pass from ever touching HDRP lighting paths.