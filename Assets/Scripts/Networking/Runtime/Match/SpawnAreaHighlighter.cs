// Assets/Scripts/Networking/Runtime/Match/SpawnAreaHighlighter.cs
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class SpawnAreaHighlighter : MonoBehaviour
{
    public enum Mode { Hidden, Choosing }
    public enum AreaRole { Friendly, Enemy, Neutral, MapBounds }

    [Header("Behavior")]
    [Tooltip("When true, ignore this object's BoxCollider during Choosing and render exactly the target Bounds sent from the controller.")]
    [SerializeField] bool followTargetBounds = true;
    [SerializeField] bool autoDetectRole = true;
    [SerializeField] AreaRole areaRole;

    // Forbidden regions to show as red (neutral + enemy lane)
    static readonly List<Bounds> s_Forbidden = new();

    [Header("Visual")]
    // 40% transparent across all area colours (alpha = 0.6). Friendly = green, Neutral/Enemy/Map = red.
    [SerializeField] Color activeColor   = new Color(0.20f, 0.95f, 0.20f, 0.60f);
    [SerializeField] Color inactiveColor = new Color(0.90f, 0.20f, 0.20f, 0.60f);
    public Color blockedColor  = new Color(1f, 0.25f, 0.25f, 0.6f);
    [SerializeField, Min(0f)] float yOffset = 0.02f;
// Sets consistent 40% transparency and colour scheme (green for own, red for others). Previously these were much more transparent.

// Ensure friendly highlight is GREEN, enemy/other is RED.
[Header("Colors")]
public Color friendlyColor = new Color(0f, 1f, 0f, 0.35f);
public Color enemyColor    = new Color(1f, 0f, 0f, 0.35f);

    static readonly List<SpawnAreaHighlighter> s_All = new();
    static Mode s_Mode = Mode.Hidden;
    static Bounds s_Target;
    static bool s_TargetBlocked;
    // Carved "holes" from blockers (world-space XZ AABBs)
    static readonly List<Bounds> s_Holes = new();
// [Highlighter] Store per-area carved rectangles.

    // New layout mode fields
    static bool s_LayoutMode = false;
    static Bounds s_FriendlyBounds;
    static Bounds s_EnemyBounds;
    static Bounds s_NeutralBounds;
    static Bounds s_MapBounds;
    static bool s_FriendlyBlocked;

    BoxCollider _col;
    MeshRenderer _mr;
    MaterialPropertyBlock _mpb;
    MeshFilter _mf;
    bool _roleResolved;

    void Awake()
    {
        _mr = GetComponentInChildren<MeshRenderer>();
        _mpb = new MaterialPropertyBlock();
    }

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
            // Prefer stencil-culling material (cuts out holes). Fallback to plain Unlit/Color.
            var cutout = Shader.Find("Unlit/SpawnAreaStencilCull");
            var mat = cutout ? new Material(cutout) : new Material(Shader.Find("Unlit/Color"));
            // Transparent overlay drawn AFTER hole masks
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;
            // Subtle inner-edge neon glow defaults
            if (mat.HasProperty("_Glow"))      mat.SetFloat("_Glow", 0.85f);
            if (mat.HasProperty("_GlowWidth")) mat.SetFloat("_GlowWidth", 0.02f);
            _mr.sharedMaterial = mat;
        }
        }
// Renders the coloured area after the stencil masks and enables a soft neon rim. Previously both used the same queue, causing unreliable ordering.

    public static void SetMode(Mode mode, Bounds target, bool targetBlocked = false, List<Bounds> holes = null, List<Bounds> forbidden = null)
    {
        s_LayoutMode = false;
        s_Mode = mode;
        s_Target = target;
        s_TargetBlocked = targetBlocked;

        s_Holes.Clear();
        if (holes != null) s_Holes.AddRange(holes);

        s_Forbidden.Clear();
        if (forbidden != null) s_Forbidden.AddRange(forbidden);

        for (int i = 0; i < s_All.Count; i++) s_All[i].Apply();
    }
// [Highlighter] New holes argument from controller.

    public static void SetLayout(Mode mode, Bounds friendly, Bounds enemy, Bounds neutral, Bounds map,
        bool friendlyBlocked = false, List<Bounds> holes = null)
    {
        s_LayoutMode = true;
        s_Mode = mode;
        s_FriendlyBounds = friendly;
        s_EnemyBounds = enemy;
        s_NeutralBounds = neutral;
        s_MapBounds = map;
        s_FriendlyBlocked = friendlyBlocked;

        s_Holes.Clear();
        if (holes != null) s_Holes.AddRange(holes);

        // Ensure a MapBounds renderer exists so the full map area is always visible.
        bool hasMapRenderer = false;
        for (int i = 0; i < s_All.Count; i++)
        {
            var h = s_All[i];
            if (!h) continue;
            if (h.areaRole == AreaRole.MapBounds) { hasMapRenderer = true; break; }
        }
        if (!hasMapRenderer && s_MapBounds.size.sqrMagnitude > 0.0001f)
        {
            var go = new GameObject("SpawnArea_MapBounds_Runtime");
            go.hideFlags = HideFlags.DontSave;
            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            var hi = go.AddComponent<SpawnAreaHighlighter>();
            hi.autoDetectRole = false;
            hi.areaRole = AreaRole.MapBounds;
            hi.followTargetBounds = true;
            if (Camera.main) go.transform.SetPositionAndRotation(s_MapBounds.center, Quaternion.identity);
        }

        for (int i = 0; i < s_All.Count; i++)
        {
            var h = s_All[i];
            if (!h) continue;
            h._roleResolved = false;
            h.Apply();
        }
    }
// Auto-spawns a MapBounds highlighter at runtime so the map area is always rendered.

    void Apply()
    {
        if (_mr == null || _col == null) return;

        if (s_Mode == Mode.Hidden) { _mr.enabled = false; ClearHoleVisuals(); return; }
        _mr.enabled = true;

        Bounds renderWorld;

        if (s_LayoutMode)
        {
            // Layout mode: each highlighter renders its assigned area
            if (!_roleResolved)
            {
                if (autoDetectRole)
                {
                    var pos = _col.bounds.center;
                    if (s_FriendlyBounds.Contains(pos)) areaRole = AreaRole.Friendly;
                    else if (s_EnemyBounds.Contains(pos)) areaRole = AreaRole.Enemy;
                    else if (s_NeutralBounds.Contains(pos)) areaRole = AreaRole.Neutral;
                    else areaRole = AreaRole.MapBounds;
                }
                _roleResolved = true;
            }

            switch (areaRole)
            {
                case AreaRole.Friendly: renderWorld = s_FriendlyBounds; followTargetBounds = true; break;
                case AreaRole.Enemy: renderWorld = s_EnemyBounds; followTargetBounds = false; break;
                case AreaRole.Neutral: renderWorld = s_NeutralBounds; followTargetBounds = false; break;
                case AreaRole.MapBounds: renderWorld = s_MapBounds; followTargetBounds = false; break;
                default: renderWorld = ToWorldBounds(_col); followTargetBounds = false; break;
            }
        }
        else
        {
            // Legacy mode
            renderWorld = followTargetBounds ? s_Target : ToWorldBounds(_col);
        }

        // Place/scale visual quad directly from the render bounds.
        _mr.transform.position = new Vector3(renderWorld.center.x, _col.bounds.center.y + yOffset, renderWorld.center.z);
        _mr.transform.localRotation = Quaternion.identity;
        _mr.transform.localScale = new Vector3(renderWorld.size.x, 1f, renderWorld.size.z);

        var mat = _mr.sharedMaterial; if (!mat) return;

        // Apply color for role and drive emission for neon glow.
        var col = ResolveColorForRole(followTargetBounds);
        mat.color = col;
        if (mat.HasProperty("_EmissionColor"))
            mat.SetColor("_EmissionColor", new Color(col.r, col.g, col.b, 0f) * 1.35f);

        // Carve holes relative to what we're rendering when highlighting the friendly area.
        if (followTargetBounds && areaRole == AreaRole.Friendly)
            BuildHoleMasks(renderWorld);
        else
            ClearHoleVisuals();
    }

    Color ResolveColorForRole(bool followsTarget)
    {
        // Treat the quad that follows the target bounds as the "friendly" highlight.
        // This avoids misclassification when auto-detecting areaRole by collider position.
        if (!followsTarget)
            return inactiveColor;

        return s_FriendlyBlocked ? blockedColor : activeColor; // green unless blocked
    }
// Friendlies now render green even if a scene highlighter sits outside the friendly split.

// Draw neutral+enemy bounds as red, outside the allowed s_Target area.
// We draw each forbidden Bounds as one quad under the green.
void BuildForbiddenVisuals(Bounds myWorld)
{
    var parent = _mr ? _mr.transform.parent : transform;
    var root = parent.Find("Forbidden");
    if (!root)
    {
        var go = new GameObject("Forbidden");
        root = go.transform;
        root.SetParent(parent, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;
    }

    int alive = 0;
    for (int i = 0; i < s_Forbidden.Count; i++)
    {
        var fb = s_Forbidden[i];
        if (fb.size.x <= 0.001f || fb.size.z <= 0.001f) continue;

        var child = (alive < root.childCount) ? root.GetChild(alive) : null;
        if (!child)
        {
            var go = new GameObject($"Forbidden_{alive}");
            child = go.transform;
            child.SetParent(root, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh ??= CreateQuadXZ();
            if (!mr.sharedMaterial)
            {
                var mat = new Material(Shader.Find("Unlit/Color"));
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 5; // draw under green
                mr.sharedMaterial = mat;
            }
        }

        child.position = new Vector3(fb.center.x, myWorld.center.y + yOffset * 0.5f, fb.center.z);
        child.localRotation = Quaternion.identity;
        child.localScale = new Vector3(fb.size.x, 1f, fb.size.z);

        var mrE = child.GetComponent<MeshRenderer>();
        if (mrE && mrE.sharedMaterial) mrE.sharedMaterial.color = blockedColor;

        alive++;
    }

    for (int i = root.childCount - 1; i >= alive; i--)
    {
        var t = root.GetChild(i);
        if (t) Destroy(t.gameObject);
    }
}

void ClearForbiddenVisuals()
{
    var root = transform.Find("Forbidden");
    if (!root) return;
    for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
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

            // Hole = stencil writer (no color) — must render BEFORE area fill for reliable stencil cull
            var maskShader = Shader.Find("Hidden/SpawnHoleMask");
            mr.sharedMaterial = maskShader ? new Material(maskShader) : new Material(Shader.Find("Unlit/Color"));
            mr.sharedMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 20;
// Forces hole masks to write stencil earlier; fixes cases where the map area appeared missing or not cut out.
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
}    void ClearHoleVisuals()
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

    // Call this whenever area role or block state changes.
    void ApplyColorNow(bool isFriendly, bool isBlocked)
    {
        if (_mr == null) return;
        var c = isBlocked ? blockedColor : (isFriendly ? friendlyColor : enemyColor);

        _mr.GetPropertyBlock(_mpb);
        // Cover common pipelines/shaders.
        if (_mr.sharedMaterial && _mr.sharedMaterial.HasProperty("_BaseColor")) _mpb.SetColor("_BaseColor", c);
        if (_mr.sharedMaterial && _mr.sharedMaterial.HasProperty("_Color"))     _mpb.SetColor("_Color", c);
        if (_mr.sharedMaterial && _mr.sharedMaterial.HasProperty("_Tint"))      _mpb.SetColor("_Tint", c);
        if (_mr.sharedMaterial && _mr.sharedMaterial.HasProperty("_EmissiveColor")) _mpb.SetColor("_EmissiveColor", c);
        _mr.SetPropertyBlock(_mpb);
    }

    // Public helper for external callers to force green for friendly.
    public void ForceFriendlyGreen(bool isBlocked)
    {
        ApplyColorNow(true, isBlocked);
    }
}