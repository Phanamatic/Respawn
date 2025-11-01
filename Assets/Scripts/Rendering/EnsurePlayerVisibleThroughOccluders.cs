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
    [SerializeField] int sortingPriority = 100;
    [SerializeField] int sortingOrder = 100;

    const string PlayerRevealLayerName = "PlayerReveal";
    int _playerRevealLayer = -1;
    bool _warnedColliderLayerConflict;

    static readonly int SP = Shader.PropertyToID("_SortingPriority");
    static readonly int TZWrite = Shader.PropertyToID("_TransparentZWrite");
    static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
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
        EnsurePlayerRevealLayer();
    }

    void LateUpdate()
    {
        EnsurePlayerRevealLayer();
        var mpb = new MaterialPropertyBlock();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (!r) continue;

            ApplyPlayerRevealLayer(r);

            // Push material knobs if available.
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!mat) continue;
                if (mat.HasProperty(SP)) mat.SetInt(SP, sortingPriority);
                if (mat.HasProperty(TZWrite)) mat.SetFloat(TZWrite, 1f);
                if (mat.HasProperty(ZWrite)) mat.SetFloat(ZWrite, 1f);
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

    void EnsurePlayerRevealLayer()
    {
        if (_playerRevealLayer >= 0) return;
        _playerRevealLayer = LayerMask.NameToLayer(PlayerRevealLayerName);
        if (_playerRevealLayer < 0)
            Debug.LogWarning("[LOS] PlayerReveal layer missing. Players may not render in reveal pass.");
    }

    void ApplyPlayerRevealLayer(Renderer r)
    {
        if (_playerRevealLayer < 0 || !r) return;
        if (r.gameObject.layer == _playerRevealLayer) return;

        if (!_warnedColliderLayerConflict && r.TryGetComponent<Collider>(out _))
        {
            _warnedColliderLayerConflict = true;
            Debug.LogWarning($"[LOS] Renderer '{r.name}' has a Collider on the same GameObject. Skipping PlayerReveal layer reassignment to avoid collider layer changes.");
            return;
        }

        r.gameObject.layer = _playerRevealLayer;
    }
}