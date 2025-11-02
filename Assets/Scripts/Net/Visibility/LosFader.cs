using System.Collections;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Client-side visual fade for remote players:
/// - Fades IN on spawn.
/// - Fades OUT on targeted request, then server hides the object.
/// Uses HDRP Unlit-compatible color (_BaseColor or _Color) via MPB.
/// </summary>
[DisallowMultipleComponent]
public sealed class LosFader : NetworkBehaviour
{
    [SerializeField] float fadeSeconds = 1f;      // default per spec
    [SerializeField] float minAlpha = 0.2f;       // darken, not fully invisible (spec #8)
    [SerializeField] Renderer[] renderers;

    MaterialPropertyBlock _mpb;
    Coroutine _co;
    float _currentAlpha = 1f;

    void Reset()
    {
        renderers = FilterNonLosRenderers(GetComponentsInChildren<Renderer>(true));
    }

    public override void OnNetworkSpawn()
    {
        if (!IsClient) return;
        if (IsOwner) return; // never fade local player
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        if (renderers == null || renderers.Length == 0) renderers = GetComponentsInChildren<Renderer>(true);
        renderers = FilterNonLosRenderers(renderers);

    ApplyAlpha(minAlpha);
    StartFade(from:minAlpha, to:1f, seconds:fadeSeconds, forceFrom:true);
    }

    void OnDisable()
    {
        if (_co != null) { StopCoroutine(_co); _co = null; }
    }

    void StartFade(float from, float to, float seconds, bool forceFrom = false)
    {
        if (_co != null) StopCoroutine(_co);
        float start = forceFrom ? Mathf.Clamp01(from) : Mathf.Clamp01(_currentAlpha);
        if (forceFrom) ApplyAlpha(start);
        if (Mathf.Approximately(start, Mathf.Clamp01(to)))
        {
            ApplyAlpha(to);
            return;
        }
        if (!isActiveAndEnabled) { ApplyAlpha(to); return; }
        _co = StartCoroutine(CoFade(start, to, Mathf.Max(0.01f, seconds)));
    }

    IEnumerator CoFade(float from, float to, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            float a = Mathf.Lerp(from, to, t / seconds);
            ApplyAlpha(a);
            yield return null;
        }
        ApplyAlpha(to);
        _co = null;
    }

    void ApplyAlpha(float a)
    {
        _currentAlpha = Mathf.Clamp01(a);
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (!r) continue;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(_mpb);
            if (r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor"))
            {
                var c = r.sharedMaterial.GetColor("_BaseColor");
                c.a = _currentAlpha;
                _mpb.SetColor("_BaseColor", c);
            }
            else if (r.sharedMaterial && r.sharedMaterial.HasProperty("_Color"))
            {
                var c = r.sharedMaterial.GetColor("_Color");
                c.a = _currentAlpha;
                _mpb.SetColor("_Color", c);
            }
            r.SetPropertyBlock(_mpb);

            // Force player later in Transparent queue.
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!mat) continue;
                if (mat.HasProperty("_SortingPriority")) mat.SetInt("_SortingPriority", 100);
                if (mat.HasProperty("_TransparentZWrite")) mat.SetFloat("_TransparentZWrite", 1f);
                if (mat.HasProperty("_EnableTransparentDepthPrepass")) mat.SetFloat("_EnableTransparentDepthPrepass", 1f);
            }
            r.sortingOrder = 100;
        }
    }

    // Called by server via LosVisibilitySystem with a targeted client.
    [ClientRpc]
    void FadeOutClientRpc(float seconds, ClientRpcParams p = default)  // unused param name required by NGO
    {
        if (!IsClient || IsOwner) return;
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        StartFade(from:1f, to:minAlpha, seconds:seconds);
    }

    // Convenience for server to send to a single viewer.
    public void RequestFadeOutTargeted(ulong viewerClientId, float seconds)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { viewerClientId } }
        };
        FadeOutClientRpc(seconds, p);
    }

    // Cancel any active fade and set to full visibility.
    public void CancelFade()
    {
        FadeToFull(0.15f);
    }

    public void FadeToFull(float seconds)
    {
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        StartFade(_currentAlpha, 1f, Mathf.Max(0.01f, seconds));
    }

    Renderer[] FilterNonLosRenderers(Renderer[] src)
    {
        if (src == null) return System.Array.Empty<Renderer>();
        var list = new System.Collections.Generic.List<Renderer>(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            var r = src[i];
            if (!r) continue;

            // Skip our LOS child by name or shader.
            if (r.gameObject.name == "__FOVMesh") continue;

            var mats = r.sharedMaterials;
            bool isLos = false;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!mat || !mat.shader) continue;
                var sn = mat.shader.name;
                if (sn == "Custom/LOS/FOVStencilWrite" || sn == "Custom/LOS/FogOfWarOverlay")
                {
                    isLos = true; break;
                }
            }
            if (!isLos) list.Add(r);
        }
        return list.ToArray();
    }
}