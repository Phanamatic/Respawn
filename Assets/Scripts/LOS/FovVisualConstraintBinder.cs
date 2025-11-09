using UnityEngine;
using UnityEngine.Animations;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Ensures the FOV visual follows the parent's POSITION but does NOT inherit ROTATION,
/// by installing a ParentConstraint on the visual child. Works in Edit & Play modes.
/// Attach this to the same GameObject that has FovMesh.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("LOS/FOV Visual Constraint (Edit & Play)")]
public sealed class FovVisualConstraintBinder : MonoBehaviour
{
    [SerializeField, Tooltip("Extra height above the player's lowest Y (collider bottom).")]
    private float visualHeightOffset = 0.03f;

    [SerializeField, Tooltip("Optional override collider; if unset, we'll auto-detect from the Source hierarchy.")]
    private Collider sourceColliderOverride;

    private Collider _sourceCollider;

    private void RefreshSourceCollider()
    {
        _sourceCollider = null;
        var tr = Source ? Source : transform;
        if (!tr) return;

        // Prefer common player colliders
        _sourceCollider = tr.GetComponent<CharacterController>();
        if (!_sourceCollider) _sourceCollider = tr.GetComponent<CapsuleCollider>();
        if (!_sourceCollider) _sourceCollider = tr.GetComponent<Collider>();

        // Fallback to children if not on the root
        if (!_sourceCollider)
        {
            _sourceCollider = tr.GetComponentInChildren<CharacterController>(true);
            if (!_sourceCollider) _sourceCollider = tr.GetComponentInChildren<CapsuleCollider>(true);
            if (!_sourceCollider) _sourceCollider = tr.GetComponentInChildren<Collider>(true);
        }
    }

    private float GetDesiredY()
    {
        var src = Source ? Source : transform;
        float baseY = src.position.y;

        var col = sourceColliderOverride ? sourceColliderOverride : _sourceCollider;
        if (col) baseY = col.bounds.min.y;

        return baseY + Mathf.Max(0f, visualHeightOffset);
    }

    [Tooltip("The child visual to constrain. If left empty, this will try to find a child named \"__FOVVisual\" or the first Renderer under this object.")]
    public Transform Visual;

    [Tooltip("Source transform to follow for position (rotation will be ignored). Defaults to this object's transform.")]
    public Transform Source;

    private void Reset()
    {
        Source = transform;
        RefreshSourceCollider();
        TryBind();
    }

    private void OnEnable()
    {
        if (Source == null) Source = transform;
        RefreshSourceCollider();
        TryBind();
    }

    private bool _deferBind;

    private void OnValidate()
    {
        // Do NOT add components during OnValidate (Unity restriction).
        // Mark for a deferred bind; we'll run it from Update()/delayCall safely.
        _deferBind = true;
    }

    private void Update()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && _deferBind)
        {
            _deferBind = false;
            TryBind();
        }
#endif
        // Keep the visual riding just above the player's lowest Y.
        if (Visual)
        {
            var p = Visual.position;
            float targetY = GetDesiredY();
            if (!Mathf.Approximately(p.y, targetY)) { p.y = targetY; Visual.position = p; }
        }
    }

    private void TryBind()
    {
        RefreshSourceCollider();

        if (Visual == null) Visual = FindVisualChild();
        if (Visual == null || Source == null) return;

        var pc = Visual.GetComponent<ParentConstraint>();
        if (pc == null) pc = Visual.gameObject.AddComponent<ParentConstraint>();

        // Fresh setup: single source = parent of the visual (usually the FovMesh root).
        for (int i = pc.sourceCount - 1; i >= 0; i--)
            pc.RemoveSource(i);

        var src = new ConstraintSource { sourceTransform = Source, weight = 1f };
        int idx = pc.AddSource(src);

        // Follow X/Z only; DO NOT inherit rotation. Y is fixed by us each frame.
        pc.translationAxis = Axis.X | Axis.Z;
        pc.rotationAxis     = 0; // Axis.None

        // Preserve offsets explicitly (no maintainOffset in Unity 6).
        var worldOffset = Visual.position - Source.position;
        pc.SetTranslationOffset(idx, worldOffset);
        pc.SetRotationOffset(idx, Vector3.zero);

        pc.locked           = true;
        pc.constraintActive = true;
        pc.enabled          = true;

        // Snap Y now so the initial pose is correct.
        var p = Visual.position;
        float targetY = GetDesiredY();
        if (!Mathf.Approximately(p.y, targetY)) { p.y = targetY; Visual.position = p; }

        // Make sure it's visible in the Inspector.
        pc.hideFlags = HideFlags.None;
        Visual.hideFlags = HideFlags.None;

#if UNITY_EDITOR
        EditorUtility.SetDirty(pc);
        EditorUtility.SetDirty(Visual);
#endif
    }

    private Transform FindVisualChild()
    {
        // 1) Exact stable name set by FovMesh
        var named = transform.Find("__FOVVisual");
        if (named) return named;

        // 2) Name heuristic: must contain BOTH "fov" and "visual"
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t == transform) continue;
            var n = t.name.ToLowerInvariant();
            if (n.Contains("fov") && n.Contains("visual")) return t;
        }

        // No tag-based lookup here — avoids spam when the tag isn't defined.
        return null;
    }
}
// Attach to the same GameObject as FovMesh. During Edit/Play, the Parent Constraint
// will appear on the child visual (e.g., "__FOVVisual"), and the mesh will no longer rotate with the parent.
