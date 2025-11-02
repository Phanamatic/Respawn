using UnityEngine;
#if UNITY_RENDER_PIPELINE_HDRP || UNITY_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

[DisallowMultipleComponent]
public sealed class PlayerLosLight : MonoBehaviour
{
    public FovMesh fovSource;
    [Range(0.1f, 5f)] public float intensity = 1.2f;
    [Range(0.25f, 2f)] public float rangeScale = 1.0f;
    public bool castShadows = true;

    [Tooltip("HDRP light layer bit (0..7). Set to -1 to hit all layers.")]
    public int environmentLightLayer = -1;

    Light _light;
#if UNITY_RENDER_PIPELINE_HDRP || UNITY_HDRP
    HDAdditionalLightData _hdData;
    const float BaseLumen = 3800f;
#endif

    void Reset() { fovSource = GetComponentInParent<FovMesh>(); }

    void OnEnable()
    {
        if (_light == null)
        {
            var go = new GameObject("LOS_PointLight");
            go.transform.SetParent(transform, false);
            _light = go.AddComponent<Light>();
            _light.type = LightType.Point;
#if UNITY_RENDER_PIPELINE_HDRP || UNITY_HDRP
            _hdData = _light.GetComponent<HDAdditionalLightData>();
            if (_hdData == null) _hdData = _light.gameObject.AddComponent<HDAdditionalLightData>();
#endif
        }
        ApplyStatic();
        ApplyDynamic();
    }

    void Update() => ApplyDynamic();

    void ApplyStatic()
    {
    _light.shadows   = castShadows ? LightShadows.Soft : LightShadows.None;
    _light.renderMode = LightRenderMode.ForcePixel;
    _light.color = new Color(1.0f, 0.95f, 0.78f);

        ApplyIntensity();

#if UNITY_RENDER_PIPELINE_HDRP || UNITY_HDRP
        if (_hdData == null)
        {
            _hdData = _light.GetComponent<HDAdditionalLightData>();
            if (_hdData == null) _hdData = _light.gameObject.AddComponent<HDAdditionalLightData>();
        }
        if (environmentLightLayer < 0)
        {
            _hdData.lightlayersMask = LightLayerEnum.Everything;
        }
        else
        {
            int layerIndex = Mathf.Clamp(environmentLightLayer, 0, 7);
            _hdData.lightlayersMask = (LightLayerEnum)(1u << layerIndex);
        }
#else
        if (environmentLightLayer < 0)
        {
            _light.renderingLayerMask = unchecked((int)uint.MaxValue);
        }
        else
        {
            int layerIndex = Mathf.Clamp(environmentLightLayer, 0, 31);
            _light.renderingLayerMask = (int)(1u << layerIndex);
        }
#endif
// [LOS] Render only on the assigned environment light layer.
    }

    void ApplyDynamic()
    {
        var r = fovSource ? fovSource.radiusMeters : 8f;
        _light.range = Mathf.Max(0.5f, r * rangeScale);
        ApplyIntensity();
    }

    void ApplyIntensity()
    {
#if UNITY_RENDER_PIPELINE_HDRP || UNITY_HDRP
        if (_hdData == null) return;
        float lumens = Mathf.Max(0.01f, intensity) * BaseLumen;
        _hdData.SetIntensity(lumens, LightUnit.Lumen);
        _light.intensity = lumens; // keep Light inspector in sync
#else
        _light.intensity = Mathf.Max(0.01f, intensity);
#endif
    }
}
// Matches light range to FOV radius and confines lighting to the Environment light layer.