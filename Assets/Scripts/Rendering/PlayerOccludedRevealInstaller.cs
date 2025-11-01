using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Ensures the HDRP custom pass that re-renders players exists at runtime.
/// </summary>
public static class PlayerOccludedRevealInstaller
{
    const string VolumeName = "PlayerOccludedRevealVolume";

    public static CustomPassVolume InstallFor(Camera cam)
    {
        if (!cam) return null;

        var existingVolumes = cam.GetComponentsInChildren<CustomPassVolume>(includeInactive: true);
        foreach (var volume in existingVolumes)
        {
            if (TryConfigure(volume))
                return volume;
        }

        var go = new GameObject(VolumeName);
        go.transform.SetParent(cam.transform, worldPositionStays: false);
        var created = go.AddComponent<CustomPassVolume>();
        created.isGlobal = true;
        created.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
        created.targetCamera = cam;
        created.priority = 100f;
        created.hideFlags = HideFlags.DontSave;

        var pass = ScriptableObject.CreateInstance<PlayerOccludedRevealPass>();
        pass.name = nameof(PlayerOccludedRevealPass);
        pass.playerLayer = GetPlayerLayerMask();

        created.customPasses = new List<CustomPass> { pass };
        return created;
    }

    static bool TryConfigure(CustomPassVolume volume)
    {
        if (!volume) return false;
        volume.isGlobal = true;
        volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
        volume.priority = Mathf.Max(volume.priority, 100f);
        if (!volume.targetCamera)
            volume.targetCamera = volume.GetComponentInParent<Camera>();

        var mask = GetPlayerLayerMask();
        var found = false;
        foreach (var pass in volume.customPasses)
        {
            if (pass is PlayerOccludedRevealPass reveal)
            {
                reveal.playerLayer = mask;
                found = true;
            }
        }

        if (!found)
        {
            var reveal = ScriptableObject.CreateInstance<PlayerOccludedRevealPass>();
            reveal.name = nameof(PlayerOccludedRevealPass);
            reveal.playerLayer = mask;
            if (volume.customPasses == null)
                volume.customPasses = new List<CustomPass>();
            volume.customPasses.Add(reveal);
        }

        return true;
    }

    static LayerMask GetPlayerLayerMask()
    {
        int layer = LayerMask.NameToLayer("PlayerReveal");
        if (layer < 0)
        {
            Debug.LogWarning("[LOS] PlayerReveal layer is missing. Custom pass will not render any objects.");
            return 0;
        }
        return 1 << layer;
    }
}
