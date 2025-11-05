using UnityEngine;

[DefaultExecutionOrder(-1000)]
public sealed class LosOverlayInstaller : MonoBehaviour
{
    void Awake()
    {
        var cam = GetComponent<Camera>();
        if (!cam)
        {
            Debug.LogWarning("[LOS] LosOverlayInstaller requires a Camera on the same GameObject.");
            return;
        }

        // Creates/attaches the full-screen overlay quad for this camera
        // and ensures the camera renders the layer used by the FOV mesh/overlay.
        FogOfWarOverlayPlane.InstallFor(cam);
        Debug.Log($"[LOS] Overlay installed on camera: {cam.name}");
    }
}
// Attach to each gameplay camera (Main Camera in Menu/Lobby/Match scenes).
// Creates the overlay at runtime so your LOS visuals always show.