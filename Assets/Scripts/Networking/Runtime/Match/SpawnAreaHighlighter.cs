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
    [SerializeField, Min(0f)] float yOffset = 0.02f;

    static readonly List<SpawnAreaHighlighter> s_All = new();
    static Mode s_Mode = Mode.Hidden;
    static Bounds s_Target;

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

    public static void SetMode(Mode mode, Bounds target)
    {
        s_Mode = mode;
        s_Target = target;
        for (int i = 0; i < s_All.Count; i++) s_All[i].Apply();
    }

    void Apply()
    {
        if (_mr == null || _col == null) return;
        _mr.transform.localPosition = _col.center + Vector3.up * yOffset;
        _mr.transform.localScale = new Vector3(_col.size.x, 1f, _col.size.z);

        if (s_Mode == Mode.Hidden) { _mr.enabled = false; return; }

        _mr.enabled = true;
        var myWorld = ToWorldBounds(_col);
        bool isMine = BoundsApproximatelyEqualXZ(myWorld, s_Target, 0.05f);
        var mat = _mr.sharedMaterial;
        if (mat) mat.color = isMine ? activeColor : inactiveColor;
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

    static Bounds ToWorldBounds(BoxCollider c)
    {
        var center = c.transform.TransformPoint(c.center);
        var extents = Vector3.Scale(c.size * 0.5f, c.transform.lossyScale);
        return new Bounds(center, extents * 2f);
    }

    static bool BoundsApproximatelyEqualXZ(Bounds a, Bounds b, float eps)
    {
        return Mathf.Abs(a.center.x - b.center.x) <= eps
            && Mathf.Abs(a.center.z - b.center.z) <= eps
            && Mathf.Abs(a.size.x - b.size.x) <= eps * 2f
            && Mathf.Abs(a.size.z - b.size.z) <= eps * 2f;
    }
}