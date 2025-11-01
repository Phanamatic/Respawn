using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Ensures the PlayerOccludedRevealPass exists at runtime so the local player remains visible through occluders.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerOccludedRevealInstaller : MonoBehaviour
{
    [SerializeField] LayerMask revealLayer = 0;

    static CustomPassVolume s_volume;

    void Awake()
    {
        EnsureInstalled(revealLayer);
    }

    public static void EnsureInstalled(LayerMask revealMask)
    {
        if (revealMask == 0) revealMask = 1 << LayerMask.NameToLayer("PlayerReveal");

        if (!s_volume)
        {
            s_volume = FindExistingVolume();
            if (!s_volume)
            {
                var go = new GameObject("PlayerOccludedRevealVolume");
                go.hideFlags = HideFlags.DontSave;
                Object.DontDestroyOnLoad(go);
                s_volume = go.AddComponent<CustomPassVolume>();
            }
        }

        s_volume.isGlobal = true;
        s_volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;

        var passes = s_volume.customPasses;
        PlayerOccludedRevealPass revealPass = null;
        for (int i = 0; i < passes.Count; i++)
        {
            revealPass = passes[i] as PlayerOccludedRevealPass;
            if (revealPass != null)
                break;
        }

        if (revealPass == null)
        {
            revealPass = ScriptableObject.CreateInstance<PlayerOccludedRevealPass>();
            revealPass.name = nameof(PlayerOccludedRevealPass);
            passes.Add(revealPass);
        }

        revealPass.revealLayer = revealMask;
    }

    static CustomPassVolume FindExistingVolume()
    {
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        var volumes = Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.InstanceID);
#else
        var volumes = Object.FindObjectsOfType<CustomPassVolume>();
#endif
        for (int i = 0; i < volumes.Length; i++)
        {
            var vol = volumes[i];
            if (!vol) continue;
            var passes = vol.customPasses;
            for (int p = 0; p < passes.Count; p++)
            {
                if (passes[p] is PlayerOccludedRevealPass)
                    return vol;
            }
        }
        return null;
    }
}
