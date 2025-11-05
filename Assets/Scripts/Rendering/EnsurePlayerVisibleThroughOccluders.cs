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
    [SerializeField] int sortingPriority = -50;  // Lower priority for player to draw later if higher means first.
    [SerializeField] int sortingOrder = -50;
    // Adjust player to lower priority/sorting to draw after occluder if needed, but test current setup. If HDRP higher priority draws first, low for player means drawn last, but for back object drawn first, so high for player. Reverse if testing shows issue.The player is not rendering behind the transparent object because the occluder is drawn before the player in the transparent queue, and if the depth write disable is not effective (e.g., material lacks the property or shader doesn't support runtime toggle), the occluder writes depth, causing the player to fail the depth test and not render. By setting a higher renderQueue for the faded occluder, we ensure it draws after the player, allowing the player to render first and the transparent occluder to blend over it without blocking. Also adjusted sortingOrder and priority for consistency in case of custom rendering or URP/HDRP differences. Test in your render pipeline to confirm order.

    static readonly int SP = Shader.PropertyToID("_SortingPriority");
    static readonly int TZWrite = Shader.PropertyToID("_TransparentZWrite");
    static readonly int TPre = Shader.PropertyToID("_EnableTransparentDepthPrepass");
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