// Assets/Scripts/Utilities/Billboard.cs
// Attach to the 3D Text object so it always faces the camera.

using UnityEngine;

[DisallowMultipleComponent]
public sealed class Billboard : MonoBehaviour
{
    [Tooltip("If enabled, billboard only rotates around Y (upright).")]
    public bool yOnly = true;

    void LateUpdate()
    {
        var cam = Camera.main; if (!cam) return;

        if (yOnly)
        {
            Vector3 dir = transform.position - cam.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
        else
        {
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }
    }
}
