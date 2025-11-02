using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local-only helper that makes tagged objects transparent when they block the view between camera and target.
/// </summary>
[DisallowMultipleComponent]
public sealed class LineOfSightTransparency : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Camera will keep visibility to this target clear.")]
    public Transform target;

    [Tooltip("Only colliders on these layers trigger transparency.")]
    public LayerMask occluderLayers = ~0;  // include Occluder + OccluderExtra in Inspector

    [Header("Raycast")]
    [Min(0f)] public float probeRadius = 0.25f;
    [Min(1)]  public int   maxHits     = 32;

    [Header("Behaviour")]
    [Range(0f, 1f)] public float occludedAlpha = 0.3f;
    [Min(0f)] public float fadeSpeed = 12f;
    [Tooltip("How often to refresh cached renderers for a tag (seconds).")]
    [Min(0.1f)] public float recacheInterval = 2f;
    [Tooltip("Keep occluders transparent for this many seconds after the ray no longer hits them.")]
    [Min(0f)] public float occlusionHoldSeconds = 0.2f;
    public bool debugRay;

    readonly Dictionary<string, TagGroup> _groups = new Dictionary<string, TagGroup>(8);
    readonly HashSet<string> _activeTags = new HashSet<string>();
    readonly List<string> _tagBuffer = new List<string>(8);

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId     = Shader.PropertyToID("_Color");
    static readonly int TintId      = Shader.PropertyToID("_TintColor");

    // Depth/ordering knobs for HDRP Transparent
    static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
    static readonly int TransparentZWriteId = Shader.PropertyToID("_TransparentZWrite");
    static readonly int EnableTransparentDepthPrepassId = Shader.PropertyToID("_EnableTransparentDepthPrepass");
    static readonly int SortingPriorityId = Shader.PropertyToID("_SortingPriority");
    static readonly int EnableTransparentDepthPostpassId = Shader.PropertyToID("_EnableTransparentDepthPostpass");

    sealed class DepthState
    {
        public Material[] mats;
        public int[] origSortingPriority;
        public int origSortingOrder;
        public int[] origRenderQueue;
    public float[] origZWrite, origTZWrite, origPrepass, origPostpass;
        public bool initialized;
        public bool appliedFaded;
    }
    static readonly Dictionary<Renderer, DepthState> _depthCache = new Dictionary<Renderer, DepthState>(64);

    RaycastHit[] _hitsBuf;
    void Awake()
    {
        _hitsBuf = new RaycastHit[Mathf.Max(8, maxHits)];
    }

    void LateUpdate()
    {
        if (!target) return;

        var origin = transform.position;
        var toTarget = target.position - origin;
        float distance = toTarget.magnitude;
        if (distance <= 0.01f) return;

        var dir = toTarget / distance;
        _activeTags.Clear();

        // Stable sample: sphere cast along sightline, ignore triggers.
        if (_hitsBuf == null || _hitsBuf.Length < maxHits) _hitsBuf = new RaycastHit[Mathf.Max(8, maxHits)];
        int hitCount = Physics.SphereCastNonAlloc(origin, probeRadius, dir, _hitsBuf, distance, occluderLayers, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            var hit = _hitsBuf[i];
            if (!hit.collider) continue;
            if (target && hit.collider.transform.IsChildOf(target)) continue;

            var go = hit.collider.gameObject;
            if ((occluderLayers.value & (1 << go.layer)) == 0) continue;

            string tag = go.tag;
            if (string.IsNullOrEmpty(tag) || tag == "Untagged") continue;

            _activeTags.Add(tag);
            EnsureTagGroup(tag);

            if (debugRay) Debug.DrawLine(origin, hit.point, Color.magenta);
        }

        _tagBuffer.Clear();
        foreach (var key in _groups.Keys) _tagBuffer.Add(key);

        for (int i = 0; i < _tagBuffer.Count; i++)
        {
            var tag = _tagBuffer[i];
            if (!_groups.TryGetValue(tag, out var group)) continue;

            bool shouldFade = _activeTags.Contains(tag);
            if (shouldFade)
            {
                group.holdUntil = Time.unscaledTime + occlusionHoldSeconds;
            }
            else if (occlusionHoldSeconds > 0f && Time.unscaledTime < group.holdUntil)
            {
                shouldFade = true;
            }

            float targetAlphaMultiplier = shouldFade ? Mathf.Clamp01(occludedAlpha) : 1f;
            UpdateGroup(group, targetAlphaMultiplier);

            if (group.entries.Count == 0 && Time.unscaledTime - group.lastPopulateTime > recacheInterval)
            {
                _groups.Remove(tag);
            }
        }
    }

    void EnsureTagGroup(string tag)
    {
        if (!_groups.TryGetValue(tag, out var group))
        {
            group = new TagGroup();
            _groups[tag] = group;
            PopulateGroup(tag, group);
            return;
        }

        group.RemoveDestroyed();
        if (group.entries.Count == 0 || Time.unscaledTime - group.lastPopulateTime >= recacheInterval)
        {
            PopulateGroup(tag, group);
        }
    }

    void PopulateGroup(string tag, TagGroup group)
    {
        group.entries.Clear();
        group.lastPopulateTime = Time.unscaledTime;
        group.holdUntil = 0f;

        Renderer[] tempRenderers = null;
        try
        {
            var taggedObjects = GameObject.FindGameObjectsWithTag(tag);
            foreach (var go in taggedObjects)
            {
                if (!go) continue;
                if ((occluderLayers.value & (1 << go.layer)) == 0) continue;

                tempRenderers = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < tempRenderers.Length; i++)
                {
                    var renderer = tempRenderers[i];
                    if (!renderer) continue;
                    if ((occluderLayers.value & (1 << renderer.gameObject.layer)) == 0) continue;
                    if (!TryCreateEntry(renderer, out var entry)) continue;
                    group.entries.Add(entry);
                }
            }
        }
        catch (UnityException)
        {
            // Tag might not exist anymore; ignore.
        }
    }

    bool TryCreateEntry(Renderer renderer, out Entry entry)
    {
        entry = null;
        if (!renderer) return false;

        var mat = renderer.sharedMaterial;
        int propertyId = -1;
        Color baseColor = Color.white;

        if (mat)
        {
            if (mat.HasProperty(BaseColorId)) { propertyId = BaseColorId; baseColor = mat.GetColor(BaseColorId); }
            else if (mat.HasProperty(ColorId)) { propertyId = ColorId; baseColor = mat.GetColor(ColorId); }
            else if (mat.HasProperty(TintId)) { propertyId = TintId; baseColor = mat.GetColor(TintId); }
        }

        if (propertyId == -1) return false;

        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);

        float baseAlpha = baseColor.a <= 0f ? 1f : Mathf.Clamp01(baseColor.a);

        entry = new Entry
        {
            renderer = renderer,
            block = block,
            colorProperty = propertyId,
            baseColor = baseColor,
            baseAlpha = baseAlpha,
            currentAlpha = baseAlpha
        };

        var color = baseColor;
        color.a = baseAlpha;
        block.SetColor(propertyId, color);
        renderer.SetPropertyBlock(block);
        return true;
    }

    void UpdateGroup(TagGroup group, float targetMultiplier)
    {
        float delta = fadeSpeed <= 0f ? 1f : fadeSpeed * Time.unscaledDeltaTime;

        // Ensure depth state matches faded/non-faded so player is visible behind.
        bool faded = targetMultiplier < 0.999f;
        for (int i = group.entries.Count - 1; i >= 0; i--)
            if (group.entries[i].renderer) EnsureTransparentNoDepth(group.entries[i].renderer, faded);

        for (int i = group.entries.Count - 1; i >= 0; i--)
        {
            var entry = group.entries[i];
            if (!entry.renderer)
            {
                group.entries.RemoveAt(i);
                continue;
            }

            float targetAlpha = entry.baseAlpha * targetMultiplier;
            float newAlpha = fadeSpeed <= 0f ? targetAlpha : Mathf.MoveTowards(entry.currentAlpha, targetAlpha, delta);

            if (Mathf.Approximately(newAlpha, entry.currentAlpha)) continue;

            entry.currentAlpha = newAlpha;
            var color = entry.baseColor;
            color.a = newAlpha;
            entry.block.SetColor(entry.colorProperty, color);
            entry.renderer.SetPropertyBlock(entry.block);

            // When restored to fully opaque, restore depth state.
            if (!faded && Mathf.Approximately(newAlpha, entry.baseAlpha))
                EnsureTransparentNoDepth(entry.renderer, false);
        }
    }

    sealed class TagGroup
    {
        public readonly List<Entry> entries = new List<Entry>();
        public float lastPopulateTime;
        public float holdUntil;

        public void RemoveDestroyed()
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].renderer) entries.RemoveAt(i);
            }
        }
    }

    sealed class Entry
    {
        public Renderer renderer;
        public MaterialPropertyBlock block;
        public int colorProperty;
        public Color baseColor;
        public float baseAlpha;
        public float currentAlpha;
    }

    // Ensure occluders do not write depth while transparent, so players behind remain visible.
    void EnsureTransparentNoDepth(Renderer r, bool faded)
    {
        if (!r) return;

        if (!_depthCache.TryGetValue(r, out var st))
        {
            st = new DepthState();
            _depthCache[r] = st;
        }

        if (!st.initialized)
        {
            // Material instances so we do not touch shared assets.
            st.mats = r.materials;
            st.origSortingPriority = new int[st.mats.Length];
            st.origRenderQueue = new int[st.mats.Length];
            st.origZWrite = new float[st.mats.Length];
            st.origTZWrite = new float[st.mats.Length];
            st.origPrepass = new float[st.mats.Length];
            st.origPostpass = new float[st.mats.Length];
            for (int i = 0; i < st.mats.Length; i++)
            {
                var m = st.mats[i];
                if (!m) continue;
                st.origSortingPriority[i] = m.HasProperty(SortingPriorityId) ? m.GetInt(SortingPriorityId) : 0;
                st.origRenderQueue[i] = m.renderQueue;
                st.origZWrite[i]          = m.HasProperty(ZWriteId) ? m.GetFloat(ZWriteId) : 0f;
                st.origTZWrite[i]         = m.HasProperty(TransparentZWriteId) ? m.GetFloat(TransparentZWriteId) : 0f;
                st.origPrepass[i]         = m.HasProperty(EnableTransparentDepthPrepassId) ? m.GetFloat(EnableTransparentDepthPrepassId) : 0f;
                st.origPostpass[i]        = m.HasProperty(EnableTransparentDepthPostpassId) ? m.GetFloat(EnableTransparentDepthPostpassId) : 0f;
            }
            st.origSortingOrder = r.sortingOrder;
            st.initialized = true;
        }

        if (faded == st.appliedFaded) return; // already applied

        if (faded)
        {
            // Disable depth writes and prepass while transparent. Draw before players.
            for (int i = 0; i < st.mats.Length; i++)
            {
                var m = st.mats[i];
                if (!m) continue;
                if (m.HasProperty(ZWriteId)) m.SetFloat(ZWriteId, 0f);
                if (m.HasProperty(TransparentZWriteId)) m.SetFloat(TransparentZWriteId, 0f);
                if (m.HasProperty(EnableTransparentDepthPrepassId)) m.SetFloat(EnableTransparentDepthPrepassId, 0f);
                if (m.HasProperty(EnableTransparentDepthPostpassId)) m.SetFloat(EnableTransparentDepthPostpassId, 0f);
                if (m.HasProperty(SortingPriorityId)) m.SetInt(SortingPriorityId, -50);

                // Draw slightly before player characters (typically queue 3000) so their rendering overwrites the faded occluder, ensuring full visibility.
                if (m.renderQueue != 2950) m.renderQueue = 2950;
            }
            r.sortingOrder = -50;
            st.appliedFaded = true;
        }
        else
        {
            // Restore original material and renderer sort/depth.
            for (int i = 0; i < st.mats.Length; i++)
            {
                var m = st.mats[i];
                if (!m) continue;
                if (m.HasProperty(ZWriteId)) m.SetFloat(ZWriteId, st.origZWrite[i]);
                if (m.HasProperty(TransparentZWriteId)) m.SetFloat(TransparentZWriteId, st.origTZWrite[i]);
                if (m.HasProperty(EnableTransparentDepthPrepassId)) m.SetFloat(EnableTransparentDepthPrepassId, st.origPrepass[i]);
                if (m.HasProperty(EnableTransparentDepthPostpassId)) m.SetFloat(EnableTransparentDepthPostpassId, st.origPostpass[i]);
                if (m.HasProperty(SortingPriorityId)) m.SetInt(SortingPriorityId, st.origSortingPriority[i]);
                m.renderQueue = st.origRenderQueue[i];
            }
            r.sortingOrder = st.origSortingOrder;
            st.appliedFaded = false;
        }
    }
}
