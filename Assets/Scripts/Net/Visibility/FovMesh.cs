using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations; // ParentConstraint
using Game.Net;
// Add Animations namespace so we can add/configure a Parent Constraint at runtime.

/// <summary>
/// Builds a 360° field-of-view mesh around the local player by raycasting against Occluder layer.
/// Renders an invisible mesh that writes stencil; the overlay darkens everything outside it.
/// Flat map assumption: XZ plane; eye height ~1.2m.
/// </summary>
[RequireComponent(typeof(Transform))]
public sealed class FovMesh : MonoBehaviour
{
    // Visual disc should always sit on this world height.
    const float VisualFixedY = -0.89f;

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
    [Tooltip("Additional height above ground to prevent Z-fighting")]
    public float groundHeightOffset = 0.01f; // sits just above ground; tweak in Inspector

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

    // Do NOT touch the owning object's rotation.
    // If this component sits on the Player root, forcing rotation here would kill mouse-aim.
    // Visual children are handled via ParentConstraint instead.
    // (If you mount FovMesh on a separate helper object, you can safely keep it identity-rotated.)

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

        // Do not modify the player's rotation here; mouse-aim logic owns yaw.
        // Keep child visuals neutral locally; the ParentConstraint on the visual prevents inheriting rotation in world space.
        if (_meshGO && _meshGO.transform.localRotation != Quaternion.identity)
            _meshGO.transform.localRotation = Quaternion.identity;
        if (_visualGO && _visualGO.transform.localRotation != Quaternion.identity)
            _visualGO.transform.localRotation = Quaternion.identity;
        // [LOS] Player yaw is untouched; visual stays world-aligned via ParentConstraint.
        // Keeps FOV world-aligned every frame even if the parent rotates mid-frame.

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
                
                // Clamp visual to sit on top of the Ground collider (never below it).
                if (_visualGO)
                {
                    var vt = _visualGO.transform;
                    var wp = vt.position;
                    // small skin avoids z-fighting; keep tiny to "sit on" the ground
                    float skin = Mathf.Clamp(groundHeightOffset, 0.001f, 0.05f);
                    Ray ray = new Ray(wp + Vector3.up * 2f, Vector3.down);
                    if (Physics.Raycast(ray, out var gHit, 10f, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Collide)
                        || (Physics.Raycast(ray, out gHit, 10f) && gHit.collider.CompareTag("Ground")))
                    {
                        wp.y = gHit.point.y + skin;
                        vt.position = wp;
                    }
                }
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

        // Raycast down to find the ground height
        float groundY = centerWS.y;
        float skinMesh = Mathf.Clamp(groundHeightOffset, 0.001f, 0.05f);
        if (Physics.Raycast(centerWS + Vector3.up * 0.5f, Vector3.down, out RaycastHit groundHit, 100f, LayerMask.GetMask("Ground")))
        {
            groundY = groundHit.point.y + skinMesh;
        }
        else
        {
            // Fallback: try with default layer if "Ground" layer doesn't exist
            if (Physics.Raycast(centerWS + Vector3.up * 0.5f, Vector3.down, out groundHit, 100f))
            {
                if (groundHit.collider.CompareTag("Ground"))
                {
                    groundY = groundHit.point.y + skinMesh;
                }
            }
        }

        int n = Mathf.Clamp(rayCount, 16, 220);
    _verts.Clear();
    _tris.Clear();
    if (_verts.Capacity < n + 1) _verts.Capacity = n + 1;
    if (_tris.Capacity < n * 3) _tris.Capacity = n * 3;

        float step = 360f / n;

        // Mesh vertices must be in LOCAL space.
        // Center vertex at ground height
    _verts.Add(new Vector3(0f, groundY - centerWS.y, 0f)); // center at ground level

        for (int i = 0; i < n; i++)
        {
            float ang = step * i * Mathf.Deg2Rad;
            var dirWS = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            Vector3 endWS = centerWS + dirWS * radiusMeters;

            if (Physics.Linecast(eyeWS, endWS + Vector3.up * EyeHeight, out var hit, occluderMask, QueryTriggerInteraction.Ignore))
            {
                endWS = hit.point;
            }

            // Place the ring vertices at ground height
            var endOnPlaneWS = new Vector3(endWS.x, groundY, endWS.z);
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
    _visualGO.transform.localPosition = new Vector3(0f, 0f, 0f);
    // Ensure a stable, searchable name for the child visual so tooling can find it.
    if (_visualGO.name != "__FOVVisual") _visualGO.name = "__FOVVisual";
    // Dev: Named child so the constraint helper can auto-bind even in Edit Mode.
        // ParentConstraint setup: ensure the visual follows position but not rotation.
        var pc = _visualGO.GetComponent<ParentConstraint>();
        if (!pc) pc = _visualGO.AddComponent<ParentConstraint>();
        for (int i = pc.sourceCount - 1; i >= 0; i--) pc.RemoveSource(i);
        var src = new ConstraintSource { sourceTransform = follow ? follow : transform, weight = 1f };
        // Add the source and capture its index so we can set offsets manually.
        var idx = pc.AddSource(src);
        // Drive position on all axes; do NOT inherit any rotation from the parent.
        // Follow only X/Z from the source; we'll clamp Y ourselves.
        pc.translationAxis = Axis.X | Axis.Z;
        pc.rotationAxis     = 0; // Axis.None
        // Preserve current world-space offset (Unity 6 ParentConstraint lacks `maintainOffset`).
        var visualT = _visualGO.transform;
        var sourceT = follow ? follow : transform;
        pc.SetTranslationOffset(idx, visualT.position - sourceT.position);
        pc.SetRotationOffset(idx, Vector3.zero);
        pc.locked           = true;
        pc.constraintActive = true;
        pc.enabled          = true;

        // Initialize world Y to the top of the Ground collider under us.
        var vt = _visualGO.transform;
        var wp = vt.position;
        float skinInit = Mathf.Clamp(groundHeightOffset, 0.001f, 0.05f);
        Ray initRay = new Ray(wp + Vector3.up * 2f, Vector3.down);
        if (Physics.Raycast(initRay, out var gHitInit, 10f, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Collide)
            || (Physics.Raycast(initRay, out gHitInit, 10f) && gHitInit.collider.CompareTag("Ground")))
        {
            wp.y = gHitInit.point.y + skinInit;
            vt.position = wp;
        }
    }

    void DisableVisualRenderer()
    {
        if (_visualGO) _visualGO.SetActive(false);
    }
}
// Keeps the stencil pass from ever touching HDRP lighting paths.