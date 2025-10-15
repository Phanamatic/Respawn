using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Game.UI.Account
{
    /// Horizontal infinite-loop ScrollRect content for profile icons.
    [RequireComponent(typeof(ScrollRect))]
    public sealed class IconCarousel : MonoBehaviour, IBeginDragHandler, IEndDragHandler, IScrollHandler
    {
        public RectTransform Content => content; // expose for inspections

        [Header("Refs")]
        [SerializeField] ScrollRect scrollRect;
        [SerializeField] RectTransform content;
        [SerializeField] float spacing = 200f;   // content HorizontalLayoutGroup spacing
        // Keep for optional override, but default to respecting prefab size.
        [SerializeField] Vector2 itemPreferredSize = new Vector2(96, 96);
        [SerializeField] bool respectPrefabSize = true;   // do NOT resize buttons by default

        [Header("Auto Scroll")]
        [SerializeField, Min(0f)] float autoScrollSpeed = 120f;   // px/s to the right
        [SerializeField, Min(0f)] float resumeDelay     = 1.25f;  // seconds after last input
        [SerializeField, Min(0f)] float smoothDamping   = 10f;    // higher = snappier
        [SerializeField, Min(2)] int repetitions = 3; // number of duplicated blocks

        HorizontalLayoutGroup _layout;
        ContentSizeFitter _fitter;
        float _blockWidth;
        bool _built;
        bool _userDragging;
        float _lastUserInputTime;

        void Reset()
        {
            scrollRect = GetComponent<ScrollRect>();
            content = transform.Find("Viewport/Content") as RectTransform;
        }

        public void Rebuild(Sprite[] sprites, ProfileIconItem prefab, Action<string, Sprite> onPicked)
        {
            if (!scrollRect) scrollRect = GetComponent<ScrollRect>();
            if (!content) content = scrollRect ? scrollRect.content : null;
            if (!scrollRect || !content || !prefab || sprites == null || sprites.Length == 0) return;

            // Configure ScrollRect for horizontal-only, unrestricted movement.
            scrollRect.horizontal = true;
            scrollRect.vertical = false;
            // Unrestricted so we can loop seamlessly without hard edges.
            scrollRect.movementType = ScrollRect.MovementType.Unrestricted;
            scrollRect.elasticity   = 0.05f;
            scrollRect.inertia      = true;
            scrollRect.decelerationRate = 0.065f;   // smoother glide
            scrollRect.scrollSensitivity = 100f;    // steadier user scroll feel

            // Configure Content as a long horizontal strip.
            content.anchorMin = new Vector2(0, 0.5f);
            content.anchorMax = new Vector2(0, 0.5f);
            content.pivot     = new Vector2(0, 0.5f);
            content.anchoredPosition = Vector2.zero;

            // Ensure layout + fitter so children lay out left-to-right.
            _layout = content.GetComponent<HorizontalLayoutGroup>();
            if (!_layout) _layout = content.gameObject.AddComponent<HorizontalLayoutGroup>();
            // Respect prefab sizing. Do not force-resize children.
            _layout.childControlWidth = false;
            _layout.childControlHeight = false;
            _layout.childForceExpandWidth = false;
            _layout.childForceExpandHeight = false;
            _layout.spacing = spacing; // 200
            _layout.padding = new RectOffset(0, 0, 0, 0);

            _fitter = content.GetComponent<ContentSizeFitter>();
            if (!_fitter) _fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            _fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            _fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize; // content matches item height

            // Clear any old items.
            for (int i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);

            // Build repeated blocks.
            repetitions = Mathf.Max(2, repetitions);
            for (int r = 0; r < repetitions; r++)
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    var spr = sprites[i];
                    if (!spr) continue;

                    var go   = Instantiate(prefab.gameObject, content, false);
                    var item = go.GetComponent<ProfileIconItem>();
                    var id   = spr.name;
                    item.Bind(id, spr, onPicked);

                    // Do not resize the original prefab unless explicitly requested.
                    if (!respectPrefabSize)
                    {
                        var rt = go.transform as RectTransform;
                        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                        if (itemPreferredSize.x > 0) le.preferredWidth  = itemPreferredSize.x;
                        if (itemPreferredSize.y > 0) le.preferredHeight = itemPreferredSize.y;
                        if (rt) rt.sizeDelta = itemPreferredSize;
                    }
                }
            }

            // Calculate one block width from first repetition.
            _blockWidth = MeasureSingleBlockWidth(sprites.Length);
            // Start at absolute left for a hard stop when scrolling left.
            var pos = content.anchoredPosition;
            pos.x = 0f;
            content.anchoredPosition = pos;

            _built = true;
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }

        float MeasureSingleBlockWidth(int itemsInBlock)
        {
            if (itemsInBlock <= 0 || content.childCount == 0) return 1f;
            // Assume uniform item width from first child.
            var first = content.GetChild(0) as RectTransform;
            float w = first ? first.rect.width : 100f;
            return _layout.padding.left + _layout.padding.right + itemsInBlock * w + (itemsInBlock - 1) * _layout.spacing;
        }

        void LateUpdate()
        {
            if (!_built || _blockWidth <= 1f) return;

            Vector2 pos = content.anchoredPosition;

            // User input window
            bool recentInput = _userDragging || (Time.unscaledTime - _lastUserInputTime) < resumeDelay;

            // Auto scroll to the left with smooth damping
            if (!recentInput && autoScrollSpeed > 0f)
            {
                float targetX = pos.x - autoScrollSpeed * Time.unscaledDeltaTime;
                float t = 1f - Mathf.Exp(-smoothDamping * Time.unscaledDeltaTime);
                pos.x = Mathf.Lerp(pos.x, targetX, t);
            }

            // Seamless infinite wrap across repeated blocks, no clamps or snaps.
            float wrap = Mathf.Max(1f, _blockWidth);
            float minX = -wrap * (repetitions - 1); // we built N blocks in a row
            if (pos.x <= minX) pos.x += wrap;       // jumped too far left -> move right by one block
            else if (pos.x >= 0f) pos.x -= wrap;    // jumped too far right -> move left by one block

            content.anchoredPosition = pos;
        }
    
    public void OnBeginDrag(PointerEventData _)
    {
        _userDragging = true;
        _lastUserInputTime = Time.unscaledTime;
    }
    public void OnEndDrag(PointerEventData _)
    {
        _userDragging = false;
        _lastUserInputTime = Time.unscaledTime;
    }
    public void OnScroll(PointerEventData _)
    {
        _lastUserInputTime = Time.unscaledTime;
    }

        // Center on the nearest duplicate of the requested id to avoid big jumps.
        public void CenterOn(string id)
        {
            if (!_built || string.IsNullOrEmpty(id)) return;
            var vp = scrollRect.viewport ? scrollRect.viewport : (RectTransform)scrollRect.transform;
            float viewHalf = vp.rect.width * 0.5f;

            float currCenter = -content.anchoredPosition.x + viewHalf;
            RectTransform best = null;
            float bestDelta = float.MaxValue;
            float bestAdjustedCenter = 0f;

            for (int i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i) as RectTransform;
                if (!child) continue;
                var pi = child.GetComponent<ProfileIconItem>();
                if (pi == null || pi.Id != id) continue;

                float baseCenter = child.anchoredPosition.x + child.rect.width * 0.5f;
                // Pick nearest copy by offsetting by whole block widths.
                int k = Mathf.RoundToInt((currCenter - baseCenter) / _blockWidth);
                float adjusted = baseCenter + k * _blockWidth;
                float d = Mathf.Abs(adjusted - currCenter);
                if (d < bestDelta) { bestDelta = d; best = child; bestAdjustedCenter = adjusted; }
            }

            if (best != null)
            {
                Vector2 pos = content.anchoredPosition;
                pos.x = -bestAdjustedCenter + viewHalf;
                content.anchoredPosition = pos;
            }
        }

    /// Highlight selection across all items.
    public void SetSelected(string id)
    {
        for (int i = 0; i < content.childCount; i++)
        {
            var pi = content.GetChild(i).GetComponent<ProfileIconItem>();
            if (!pi) continue;
            pi.SetSelected(pi.Id == id);
        }
    }
    }
}
// Creates a long horizontal strip and wraps anchoredPosition to loop infinitely.