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
    [Range(16, 512)] public int rayCount = 350;
    public LayerMask occluderMask;
    [Tooltip("Optional anchor for the FOV center (use the visual root).")]
    public Transform follow;
    public bool showFill = true;
    public Color fillColor = new Color(1.0f, 0.98f, 0.85f, 0.40f);
    [Range(0.1f, 3f)] public float fillIntensity = 1.15f;
    [Range(0f, 0.5f)] public float edgeFeather = 0.15f;

    const float EyeHeight = 1.2f;
    const float RebuildHz = 20f;

    static readonly int _ShowFillId  = Shader.PropertyToID("_ShowFill");
    static readonly int _FillMulId   = Shader.PropertyToID("_FillMul");
    static readonly int _FeatherId   = Shader.PropertyToID("_Feather");

    Mesh _mesh;
    GameObject _meshGO;
    MeshFilter _mf;
    MeshRenderer _mr;
    float _accum;
    static Material _stencilMat;

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
        _mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        _mr.allowOcclusionWhenDynamic = false;
        _mr.enabled = (_mr.sharedMaterial != null);
    }

    void OnDisable()
    {
        if (_mf) _mf.sharedMesh = null;
        if (_meshGO) _meshGO.SetActive(false);
    }

    private void Update()
    {
        if (follow != null)
        {
            transform.position = follow.position;
            transform.rotation = follow.rotation;
        }

        // update fill properties on material
        if (_mr != null)
        {
            var mpb = new MaterialPropertyBlock();
            _mr.GetPropertyBlock(mpb);
            mpb.SetFloat(_ShowFillId, showFill ? 1.0f : 0.0f);
            mpb.SetColor("_FillColor", fillColor);
            mpb.SetFloat(_FillMulId, Mathf.Max(0.1f, fillIntensity));
            mpb.SetFloat(_FeatherId, Mathf.Max(0f, edgeFeather));
            _mr.SetPropertyBlock(mpb);
        }

        if (_meshGO && !_meshGO.activeSelf) _meshGO.SetActive(true);

        _accum += Time.unscaledDeltaTime;
        if (_accum < 1f / RebuildHz) return;
        _accum = 0f;
        RebuildMesh();
    }

    void RebuildMesh()
    {
        if (_mesh == null) return;

        // World-space probe for occlusion.
        var centerWS = follow ? follow.position : transform.position;
        var eyeWS = centerWS + Vector3.up * EyeHeight;

        int n = Mathf.Max(16, rayCount);
        var verts = new List<Vector3>(n + 1);
        var tris = new List<int>(n * 3);

        float step = 360f / n;

        // Mesh vertices must be in LOCAL space.
        verts.Add(new Vector3(0f, 0.05f, 0f)); // center at player origin

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
            verts.Add(endLS);
        }

        for (int i = 0; i < n - 1; i++)
        {
            tris.Add(0); tris.Add(i + 1); tris.Add(i + 2);
        }
        tris.Add(0); tris.Add(n); tris.Add(1);

        _mesh.Clear();
        _mesh.SetVertices(verts);
        _mesh.SetTriangles(tris, 0, true);
        _mesh.RecalculateBounds();
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (!c) c = go.AddComponent<T>();
        return c;
    }
}