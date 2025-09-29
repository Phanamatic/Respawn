#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public sealed class LiveGamePreviewWindow : EditorWindow
{
    Camera _camera;
    RenderTexture _rt;
    bool _autoMain = true;
    bool _live = true;

    [MenuItem("Window/Rendering/Live Game Preview")]
    public static void Open() => GetWindow<LiveGamePreviewWindow>("Live Game Preview");

    void OnEnable()  { EditorApplication.update += EditorTick; }
    void OnDisable() { EditorApplication.update -= EditorTick; ReleaseRT(); }

    void EditorTick()
    {
        if (!Application.isPlaying && _live) Repaint();
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _autoMain = GUILayout.Toggle(_autoMain, "Auto Main Camera", EditorStyles.toolbarButton);
            _live     = GUILayout.Toggle(_live, "Live", EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
        }

        EditorGUI.BeginDisabledGroup(_autoMain);
        _camera = (Camera)EditorGUILayout.ObjectField("Camera", _autoMain ? Camera.main : _camera, typeof(Camera), true);
        EditorGUI.EndDisabledGroup();

        if (_autoMain) _camera = Camera.main;

        var r = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (Event.current.type == EventType.Repaint)
        {
            EnsureRT(Mathf.Max(1, (int)r.width), Mathf.Max(1, (int)r.height));
            RenderToRT();
            if (_rt) GUI.DrawTexture(r, _rt, ScaleMode.StretchToFill, false);
        }
    }

    void EnsureRT(int w, int h)
    {
        if (_rt != null && (_rt.width != w || _rt.height != h)) ReleaseRT();
        if (_rt == null)
        {
            var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR)
                ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            _rt = new RenderTexture(w, h, 24, fmt) { name = "LiveGamePreview_RT" };
            _rt.Create();
        }
    }

    void ReleaseRT()
    {
        if (_rt != null) { _rt.Release(); DestroyImmediate(_rt); _rt = null; }
    }

    void RenderToRT()
    {
        if (!_camera || !_rt) return;

        var prevActive = RenderTexture.active;
        var prevTarget = _camera.targetTexture;

        _camera.targetTexture = _rt;
        _camera.Render();  
        _camera.targetTexture = prevTarget;

        RenderTexture.active = prevActive;
    }
}
#endif
