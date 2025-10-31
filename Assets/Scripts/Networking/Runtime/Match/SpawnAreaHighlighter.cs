// Assets/Scripts/Networking/Runtime/Match/SpawnAreaHighlighter.cs
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class SpawnAreaHighlighter : MonoBehaviour
{
    public enum Mode { Hidden, Choosing }

    public enum AreaRole
    {
        Friendly,
        Enemy,
        Neutral,
        MapBounds
    }

    [Header("Behavior")]
    [Tooltip("When true, ignore this object's BoxCollider during Choosing and render exactly the target Bounds sent from the controller.")]
    [SerializeField] bool followTargetBounds = true;

    [Header("Visual")]
    [SerializeField] Color activeColor = new Color(0.2f, 0.8f, 0.2f, 0.28f);
    [SerializeField] Color inactiveColor = new Color(0.8f, 0.2f, 0.2f, 0.14f);
    [SerializeField] Color blockedColor = new Color(0.8f, 0.2f, 0.2f, 0.35f);
    [SerializeField, Min(0f)] float yOffset = 0.02f;

    [Header("Role")]
    [SerializeField] AreaRole areaRole = AreaRole.Friendly;
    [SerializeField] bool autoDetectRole = true;

    static readonly List<SpawnAreaHighlighter> s_All = new();
    static Mode s_Mode = Mode.Hidden;
    static Bounds s_FriendlyBounds;
    static Bounds s_EnemyBounds;
    static Bounds s_NeutralBounds;
    static Bounds s_MapBounds;
    static bool s_FriendlyBlocked;
    // Carved "holes" from blockers (world-space XZ AABBs)
    static readonly List<Bounds> s_Holes = new();
// [Highlighter] Store per-area carved rectangles.

    BoxCollider _col;
    MeshRenderer _mr;
    MeshFilter _mf;
    bool _roleResolved;

    void OnEnable()
    {
        _col = GetComponent<BoxCollider>();
        Build();
        if (!s_All.Contains(this)) s_All.Add(this);
        _roleResolved = false;
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
            // Prefer stencil-culling material (cuts out holes). Fallback to plain Unlit/Color.
            var cutout = Shader.Find("Unlit/SpawnAreaStencilCull");
            var mat = cutout ? new Material(cutout) : new Material(Shader.Find("Unlit/Color"));
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _mr.sharedMaterial = mat;
        }
    }

    public static void SetLayout(Mode mode, Bounds friendly, Bounds enemy, Bounds neutral, Bounds map,
        bool friendlyBlocked = false, List<Bounds> holes = null)
    {
        s_Mode = mode;
        s_FriendlyBounds = friendly;
        s_EnemyBounds = enemy;
        s_NeutralBounds = neutral;
        s_MapBounds = map;
        s_FriendlyBlocked = friendlyBlocked;

        s_Holes.Clear();
        if (holes != null) s_Holes.AddRange(holes);

        for (int i = 0; i < s_All.Count; i++)
        {
            var h = s_All[i];
            if (!h) continue;
            h._roleResolved = false;
            h.Apply();
        }
    }

    void Apply()
    {
        if (_mr == null || _col == null) return;

        if (s_Mode == Mode.Hidden) { _mr.enabled = false; ClearHoleVisuals(); return; }
        _mr.enabled = true;

        if (!_roleResolved && autoDetectRole)
        {
            areaRole = GuessRoleFromCollider(areaRole);
            _roleResolved = true;
        }

        if (!TryGetBoundsForRole(out var target))
        {
            _mr.enabled = false;
            ClearHoleVisuals();
            return;
        }

        // Either render our own collider (legacy) or the layout's bounds for our role.
        Bounds renderWorld = followTargetBounds ? target : ToWorldBounds(_col);
        if (renderWorld.size.x <= 0.001f || renderWorld.size.z <= 0.001f)
        {
            _mr.enabled = false;
            ClearHoleVisuals();
            return;
        }

        // Place/scale visual quad directly from the render bounds.
        _mr.transform.position = new Vector3(renderWorld.center.x, _col.bounds.center.y + yOffset, renderWorld.center.z);
        _mr.transform.localRotation = Quaternion.identity;
        _mr.transform.localScale = new Vector3(renderWorld.size.x, 1f, renderWorld.size.z);

        var mat = _mr.sharedMaterial; if (!mat) return;
        mat.color = ResolveColorForRole(followTargetBounds);

        // Carve holes relative to what we're rendering when highlighting the friendly area.
        if (followTargetBounds && areaRole == AreaRole.Friendly)
            BuildHoleMasks(renderWorld);
        else
            ClearHoleVisuals();
    }

// Build child quads that write **stencil only** (no color), so the area material can cull them.
void BuildHoleMasks(Bounds myWorld)
{
    if (myWorld.size.x <= 0.001f || myWorld.size.z <= 0.001f) { ClearHoleVisuals(); return; }

    var parent = _mr.transform;
    var holesRoot = parent.Find("Holes");
    if (!holesRoot)
    {
        var go = new GameObject("Holes");
        holesRoot = go.transform;
        holesRoot.SetParent(parent, false);
        holesRoot.localPosition = new Vector3(0f, 0.01f, 0f); // slight lift, no z-fight
        holesRoot.localRotation = Quaternion.identity;
        holesRoot.localScale    = Vector3.one;
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

            // Hole = stencil writer (no color)
            var maskShader = Shader.Find("Hidden/SpawnHoleMask");
            mr.sharedMaterial = maskShader ? new Material(maskShader) : new Material(Shader.Find("Unlit/Color"));
            mr.sharedMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // Normalize to parent space
        float nx = inter.size.x / myWorld.size.x;
        float nz = inter.size.z / myWorld.size.z;
        float px = (inter.center.x - myWorld.center.x) / myWorld.size.x;
        float pz = (inter.center.z - myWorld.center.z) / myWorld.size.z;

        child.localPosition = new Vector3(px, 0f, pz);
        child.localScale    = new Vector3(nx, 1f, nz);

        alive++;
    }

    for (int i = holesRoot.childCount - 1; i >= alive; i--)
    {
        var t = holesRoot.GetChild(i);
        if (t) Destroy(t.gameObject);
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
            if (t) Destroy(t.gameObject);
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

    bool TryGetBoundsForRole(out Bounds bounds)
    {
        bounds = default;
        switch (areaRole)
        {
            case AreaRole.Friendly: bounds = s_FriendlyBounds; break;
            case AreaRole.Enemy:    bounds = s_EnemyBounds;   break;
            case AreaRole.Neutral:  bounds = s_NeutralBounds; break;
            case AreaRole.MapBounds:bounds = s_MapBounds;     break;
            default: bounds = s_FriendlyBounds; break;
        }

        if (!followTargetBounds)
            return true;

        return bounds.size.x > 0.001f && bounds.size.z > 0.001f;
    }

    Color ResolveColorForRole(bool usingLayoutBounds)
    {
        if (!usingLayoutBounds)
            return inactiveColor;

        return areaRole switch
        {
            AreaRole.Friendly => s_FriendlyBlocked ? blockedColor : activeColor,
            AreaRole.Enemy    => inactiveColor,
            AreaRole.Neutral  => inactiveColor,
            AreaRole.MapBounds=> inactiveColor,
            _ => inactiveColor
        };
    }

    AreaRole GuessRoleFromCollider(AreaRole fallback)
    {
        if (!_col) return fallback;

        var world = ToWorldBounds(_col);
        if (world.size.sqrMagnitude <= 0.0001f)
            return fallback;

        AreaRole bestRole = fallback;
        float bestScore = float.PositiveInfinity;

        void Consider(AreaRole role, Bounds target)
        {
            if (target.size.sqrMagnitude <= 0.0001f) return;
            float score = ScoreBounds(world, target);
            if (score < bestScore)
            {
                bestScore = score;
                bestRole = role;
            }
        }

        Consider(AreaRole.Friendly, s_FriendlyBounds);
        Consider(AreaRole.Enemy, s_EnemyBounds);
        Consider(AreaRole.Neutral, s_NeutralBounds);
        Consider(AreaRole.MapBounds, s_MapBounds);

        return bestRole;
    }

    static float ScoreBounds(Bounds source, Bounds target)
    {
        var sizeDiff = Mathf.Abs(source.size.x - target.size.x) + Mathf.Abs(source.size.z - target.size.z);
        var src = new Vector2(source.center.x, source.center.z);
        var dst = new Vector2(target.center.x, target.center.z);
        float centerDiff = Vector2.Distance(src, dst);
        return sizeDiff + centerDiff;
    }

    static Bounds ToWorldBounds(BoxCollider c)
    {
        // Use Unity's world AABB (handles rotation and scale).
        return c.bounds;
    }

    static bool ContainsXZ(Bounds b, Vector3 p)
    {
        return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
    }

}