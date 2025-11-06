using UnityEngine;

/// <summary>
/// Guarantees player renderers remain visible through transparent occluders:
/// - Depth prepass + depth write on
/// - High sorting priority and sorting order
/// Skips LOS helper meshes.
/// </summary>
[DisallowMultipleComponent]
public sealed class EnsurePlayerVisibleThroughOccluders : MonoBehaviour
{
    [SerializeField] Renderer[] renderers;
    [SerializeField] int sortingPriority = 50;   // Positive priority so players sort after faded occluders.
    [SerializeField] int sortingOrder = 50;

    static readonly int SP = Shader.PropertyToID("_SortingPriority");
    static readonly int TZWrite = Shader.PropertyToID("_TransparentZWrite");
    static readonly int TPre = Shader.PropertyToID("_EnableTransparentDepthPrepass");
    static readonly int TPost = Shader.PropertyToID("_EnableTransparentDepthPostpass");
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    void Reset()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        Filter();
    }

    void OnEnable()
    {
        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);
        Filter();
    }

    void LateUpdate()
    {
        var mpb = new MaterialPropertyBlock();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (!r) continue;

            // Push material knobs if available.
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!mat) continue;
                if (mat.HasProperty(SP)) mat.SetInt(SP, sortingPriority);
                if (mat.HasProperty(TZWrite)) mat.SetFloat(TZWrite, 1f);
                if (mat.HasProperty(TPre)) mat.SetFloat(TPre, 1f);
                if (mat.HasProperty(TPost)) mat.SetFloat(TPost, 1f);
            }
            r.sortingOrder = sortingOrder;

            // Ensure alpha = 1 on common slots to avoid accidental fade.
            r.GetPropertyBlock(mpb);
            if (r.sharedMaterial && r.sharedMaterial.HasProperty(BaseColor))
            {
                var c = r.sharedMaterial.GetColor(BaseColor); c.a = 1f;
                mpb.SetColor(BaseColor, c);
            }
            if (r.sharedMaterial && r.sharedMaterial.HasProperty(ColorId))
            {
                var c2 = r.sharedMaterial.GetColor(ColorId); c2.a = 1f;
                mpb.SetColor(ColorId, c2);
            }
            r.SetPropertyBlock(mpb);
        }
    }

    void Filter()
    {
        if (renderers == null) return;
        var list = new System.Collections.Generic.List<Renderer>(renderers.Length);
        foreach (var r in renderers)
        {
            if (!r) continue;
            if (r.gameObject.name == "__FOVMesh") continue;
            var mats = r.sharedMaterials;
            bool isLos = false;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!mat || !mat.shader) continue;
                var sn = mat.shader.name;
                if (sn == "Custom/LOS/FOVStencilWrite" || sn == "Custom/LOS/FogOfWarOverlay")
                { isLos = true; break; }
            }
            if (!isLos) list.Add(r);
        }
        renderers = list.ToArray();
    }
}