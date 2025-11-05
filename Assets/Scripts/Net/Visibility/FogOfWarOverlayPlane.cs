using UnityEngine;

/// <summary>
/// Full-screen dark overlay that renders after the scene and respects the FOV stencil:
/// Only pixels where stencil != 1 are darkened.
/// </summary>
public sealed class FogOfWarOverlayPlane : MonoBehaviour
{
    Camera _cam;
    static Material _overlayMat;
    const float Dist = 0.5f;

    public static void InstallFor(Camera cam)
    {
        // Ensure the overlay always uses the correct Unlit material and never falls back to HDRP/Lit.
        // If the stencil overlay material isn't found, we *disable* the renderer to avoid Lit fallback.
        if (!cam) return;
        if (cam.GetComponentInChildren<FogOfWarOverlayPlane>()) return;

        var go = new GameObject("FogOfWarOverlayPlane");
        go.transform.SetParent(cam.transform, false);

        // Put overlay on a guaranteed-visible layer and force camera to render it.
        int fovLayer = LayerMask.NameToLayer("Ignore Raycast");
        if (fovLayer < 0) fovLayer = 0; // Default fallback
        go.layer = fovLayer;
        EnsureLayerVisible(cam, fovLayer);

        var comp = go.AddComponent<FogOfWarOverlayPlane>();
        comp._cam = cam;
        comp.EnsureMesh(go);
    }

    static void EnsureLayerVisible(Camera cam, int layer)
    {
        int mask = cam.cullingMask;
        int bit = 1 << layer;
        if ((mask & bit) == 0) cam.cullingMask = mask | bit;
    }

    void EnsureMesh(GameObject go)
    {
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        // Load the overlay material exactly as expected
        var overlayMat = Resources.Load<Material>("LOS/FogOfWarOverlayMat");
        if (overlayMat == null)
        {
            // As a last resort try the shader and make a runtime material
            var sh = Shader.Find("Custom/LOS/FogOfWarOverlay");
            if (sh != null) overlayMat = new Material(sh);
        }

        if (overlayMat == null)
        {
            Debug.LogError("[LOS] FogOfWarOverlayMat missing. Overlay rendering disabled to avoid HDRP/Lit fallback.");
            if (mr) mr.enabled = false;
            return;
        }

        // Force a single material slot with the overlay mat, and strip any extras
        mr.sharedMaterials = new[] { overlayMat };

        // Absolute safety: this renderer must never request lighting buffers
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
#if UNITY_6000_0_OR_NEWER
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
#endif
        mr.allowOcclusionWhenDynamic = false;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

        // Ensure overlay uses the same layer as the GameObject (already set in InstallFor)
        mr.gameObject.layer = go.layer;

        // Simple quad
        var m = new Mesh { name = "FogOverlayQuad" };
        m.SetVertices(new Vector3[] {
            new Vector3(-1,-1,0), new Vector3(1,-1,0),
            new Vector3(1, 1,0), new Vector3(-1, 1,0)
        });
        m.SetTriangles(new int[]{ 0,1,2, 0,2,3 }, 0);
        m.RecalculateBounds();
        mf.sharedMesh = m;
    }

    void LateUpdate()
    {
        if (!_cam) { Destroy(gameObject); return; }

        var t = transform;
        t.localRotation = Quaternion.identity;

        if (_cam.orthographic)
        {
            // Cover the full ortho view. Place just in front of near clip.
            float near = Mathf.Max(0.01f, _cam.nearClipPlane + 0.01f);
            t.localPosition = new Vector3(0, 0, near);

            float h = 2f * _cam.orthographicSize;
            float w = h * _cam.aspect;

            // Our quad is [-1,1] so scale by half extents.
            t.localScale = new Vector3(w * 0.5f, h * 0.5f, 1f);
        }
        else
        {
            // Perspective sizing using frustum at Dist.
            float dist = Mathf.Max(0.1f, _cam.nearClipPlane + 0.05f);
            t.localPosition = new Vector3(0, 0, dist);

            float h = 2f * dist * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float w = h * _cam.aspect;

            // Quad is [-1,1], scale by half extents.
            t.localScale = new Vector3(w * 0.5f, h * 0.5f, 1f);
        }
    }
}
// Prevents the HDRP 'g_vLightListCluster' error by guaranteeing an Unlit overlay or nothing.