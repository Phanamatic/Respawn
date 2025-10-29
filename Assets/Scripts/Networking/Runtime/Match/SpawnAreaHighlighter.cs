// Assets/Scripts/Networking/Runtime/Match/SpawnAreaHighlighter.cs
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class SpawnAreaHighlighter : MonoBehaviour
{
    public enum Mode { Hidden, Choosing }

    [Header("Visual")]
    [SerializeField] Color activeColor = new Color(0.2f, 0.8f, 0.2f, 0.28f);
    [SerializeField] Color inactiveColor = new Color(0.8f, 0.2f, 0.2f, 0.14f);
    [SerializeField] Color blockedColor = new Color(0.8f, 0.2f, 0.2f, 0.35f);
    [SerializeField, Min(0f)] float yOffset = 0.02f;

    static readonly List<SpawnAreaHighlighter> s_All = new();
    static Mode s_Mode = Mode.Hidden;
    static Bounds s_Target;
    static bool s_TargetBlocked;
    // Carved "holes" from blockers (world-space XZ AABBs)
    static readonly List<Bounds> s_Holes = new();
// [Highlighter] Store per-area carved rectangles.

    BoxCollider _col;
    MeshRenderer _mr;
    MeshFilter _mf;

    void OnEnable()
    {
        _col = GetComponent<BoxCollider>();
        Build();
        if (!s_All.Contains(this)) s_All.Add(this);
        Apply();
    }

    void OnDisable() { s_All.Remove(this); }

    void Start()
    {
        AssignMeshAndMat();
        // Ensure first-round visuals apply even if SetMode was already called.
        Apply();
    }

    void Build()
    {
        var child = transform.Find("AreaVisual");
        if (!child)
        {
            var go = new GameObject("AreaVisual");
            go.transform.SetParent(transform, worldPositionStays: false);
            child = go.transform;
        }

        child.localRotation = Quaternion.identity;
        child.localPosition = _col.center + Vector3.up * yOffset;
        child.localScale = new Vector3(_col.size.x, 1f, _col.size.z);

        _mf = child.GetComponent<MeshFilter>();
        if (_mf == null) _mf = child.gameObject.AddComponent<MeshFilter>();

        _mr = child.GetComponent<MeshRenderer>();
        if (_mr == null) _mr = child.gameObject.AddComponent<MeshRenderer>();
    }

    private void AssignMeshAndMat()
    {
        if (_mf == null) { Debug.LogError("MeshFilter is null after adding."); return; }
        if (_mf.sharedMesh == null) _mf.sharedMesh = CreateQuadXZ();

        if (_mr == null) { Debug.LogError("MeshRenderer is null after adding."); return; }
        if (_mr.sharedMaterial == null)
        {
            var shader = Shader.Find("Unlit/Color");
            if (shader == null) { Debug.LogError("Unlit/Color shader not found."); return; }
            var mat = new Material(shader);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _mr.sharedMaterial = mat;
        }
    }

    public static void SetMode(Mode mode, Bounds target, bool targetBlocked = false, List<Bounds> holes = null)
    {
        s_Mode = mode;
        s_Target = target;
        s_TargetBlocked = targetBlocked;

        s_Holes.Clear();
        if (holes != null) s_Holes.AddRange(holes);

        for (int i = 0; i < s_All.Count; i++) s_All[i].Apply();
    }
// [Highlighter] New holes argument from controller.

    void Apply()
    {
        if (_mr == null || _col == null) return;
        _mr.transform.localPosition = _col.center + Vector3.up * yOffset;
        _mr.transform.localScale   = new Vector3(_col.size.x, 1f, _col.size.z);

        if (s_Mode == Mode.Hidden) { _mr.enabled = false; ClearHoleVisuals(); return; }

        _mr.enabled = true;
        var myWorld = ToWorldBounds(_col);                 // world AABB
        bool isMine = ContainsXZ(myWorld, s_Target.center)
                   && SizesRoughlyMatchXZ(myWorld.size, s_Target.size);
        var mat = _mr.sharedMaterial;
        if (!mat) return;

        mat.color = (isMine && s_TargetBlocked) ? blockedColor : (isMine ? activeColor : inactiveColor);

        // Carved red "holes" only for the active player's area
        if (isMine) BuildHoleVisuals(myWorld);
        else ClearHoleVisuals();
// [Highlighter] Paint red quads in the carved regions.
    }

    // Creates/updates child quads that visualize red "holes" carved by blockers.
    void BuildHoleVisuals(Bounds myWorld)
    {
        var parent = _mr.transform;
        var holesRoot = parent.Find("Holes");
        if (!holesRoot)
        {
            var go = new GameObject("Holes");
            holesRoot = go.transform;
            holesRoot.SetParent(parent, false);
            holesRoot.localPosition = Vector3.zero;
            holesRoot.localRotation = Quaternion.identity;
            holesRoot.localScale = Vector3.one;
        }

        int alive = 0;
        for (int i = 0; i < s_Holes.Count; i++)
        {
            var inter = IntersectXZ(myWorld, s_Holes[i]);
            if (inter.size.x <= 0.001f || inter.size.z <= 0.001f) continue;

            var child = (alive < holesRoot.childCount) ? holesRoot.GetChild(alive) : null;
            if (child == null)
            {
                var go = new GameObject($"Hole_{alive}");
                child = go.transform;
                child.SetParent(holesRoot, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                if (!mf.sharedMesh) mf.sharedMesh = CreateQuadXZ();
                if (!mr.sharedMaterial)
                {
                    var shader = Shader.Find("Unlit/Color");
                    var mat = new Material(shader); mat.SetInt("_ZWrite", 0); mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    mr.sharedMaterial = mat;
                }
            }

            child.localPosition = new Vector3(inter.center.x - myWorld.center.x, 0f, inter.center.z - myWorld.center.z);
            child.localScale    = new Vector3(inter.size.x, 1f, inter.size.z);

            var mrExisting = child.GetComponent<MeshRenderer>();
            if (mrExisting && mrExisting.sharedMaterial) mrExisting.sharedMaterial.color = blockedColor;

            alive++;
        }

        // Remove extras
        for (int i = holesRoot.childCount - 1; i >= alive; i--)
        {
            var t = holesRoot.GetChild(i);
            if (t) DestroyImmediate(t.gameObject);
        }
    }

    void ClearHoleVisuals()
    {
        if (_mr == null) return;
        var holesRoot = _mr.transform.Find("Holes");
        if (!holesRoot) return;
        for (int i = holesRoot.childCount - 1; i >= 0; i--)
        {
            var t = holesRoot.GetChild(i);
            if (t) DestroyImmediate(t.gameObject);
        }
    }

    static Mesh CreateQuadXZ()
    {
        var m = new Mesh { name = "SpawnAreaQuadXZ" };
        m.vertices = new[]
        {
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f,  0.5f),
            new Vector3( 0.5f, 0f,  0.5f),
            new Vector3( 0.5f, 0f, -0.5f),
        };
        m.uv = new[] { new Vector2(0,0), new Vector2(0,1), new Vector2(1,1), new Vector2(1,0) };
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateNormals();
        return m;
    }

    static Bounds IntersectXZ(Bounds a, Bounds b)
    {
        var min = new Vector3(Mathf.Max(a.min.x, b.min.x), 0f, Mathf.Max(a.min.z, b.min.z));
        var max = new Vector3(Mathf.Min(a.max.x, b.max.x), 0f, Mathf.Min(a.max.z, b.max.z));
        if (max.x < min.x || max.z < min.z) return new Bounds(Vector3.zero, Vector3.zero);
        var size = new Vector3(max.x - min.x, 0f, max.z - min.z);
        var center = new Vector3(min.x + size.x * 0.5f, 0f, min.z + size.z * 0.5f);
        return new Bounds(center, size);
    }
// [Highlighter] Adds hole quads and intersection helper.

    static Bounds ToWorldBounds(BoxCollider c)
    {
        // Use Unity's world AABB (handles rotation and scale).
        return c.bounds;
    }

    static bool ContainsXZ(Bounds b, Vector3 p)
    {
        return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
    }

    static bool SizesRoughlyMatchXZ(Vector3 a, Vector3 b)
    {
        // Tolerate authoring/scale differences
        float eps = Mathf.Max(0.5f, 0.1f * Mathf.Max(a.x, a.z));
        return Mathf.Abs(a.x - b.x) <= eps && Mathf.Abs(a.z - b.z) <= eps;
    }
}