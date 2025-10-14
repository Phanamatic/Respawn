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
        [SerializeField] Vector2 itemPreferredSize = new Vector2(96, 96); // applied to each item (LayoutElement)

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
            // Clamp to bounds for hard left stop, keep inertia for smooth feel.
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.elasticity   = 0.05f;
            scrollRect.inertia      = true;
            scrollRect.scrollSensitivity = 200f; // faster mouse wheel scroll

            // Configure Content as a long horizontal strip.
            content.anchorMin = new Vector2(0, 0.5f);
            content.anchorMax = new Vector2(0, 0.5f);
            content.pivot     = new Vector2(0, 0.5f);
            content.anchoredPosition = Vector2.zero;

            // Ensure layout + fitter so children lay out left-to-right.
            _layout = content.GetComponent<HorizontalLayoutGroup>();
            if (!_layout) _layout = content.gameObject.AddComponent<HorizontalLayoutGroup>();
            _layout.childControlWidth = true;
            _layout.childControlHeight = true;
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

                    // Ensure each item reports a preferred size to the layout.
                    var rt = go.transform as RectTransform;
                    var le = go.GetComponent<LayoutElement>();
                    if (!le) le = go.AddComponent<LayoutElement>();
                    if (itemPreferredSize.x > 0) le.preferredWidth  = itemPreferredSize.x;
                    if (itemPreferredSize.y > 0) le.preferredHeight = itemPreferredSize.y;
                    if (rt) rt.sizeDelta = itemPreferredSize;
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

            var vp  = scrollRect.viewport ? scrollRect.viewport : (RectTransform)scrollRect.transform;
            float contentWidth  = content.rect.width;
            float viewportWidth = vp.rect.width;
            float leftBoundX    = 0f; // hard stop at far left
            float rightBoundX   = Mathf.Min(0f, viewportWidth - contentWidth); // negative

            Vector2 pos = content.anchoredPosition;

            bool recentInput = _userDragging || (Time.unscaledTime - _lastUserInputTime) < resumeDelay;

            if (!recentInput)
            {
                float targetX = pos.x - autoScrollSpeed * Time.unscaledDeltaTime;
                if (targetX <= rightBoundX) targetX = leftBoundX;
                float t = 1f - Mathf.Exp(-smoothDamping * Time.unscaledDeltaTime);
                pos.x = Mathf.Lerp(pos.x, targetX, t);
            }

            if (pos.x > leftBoundX) pos.x = leftBoundX;
            if (pos.x < rightBoundX) pos.x = rightBoundX;

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

    // Center the view on the first item with the given id.
    public void CenterOn(string id)
    {
        if (!_built || string.IsNullOrEmpty(id)) return;
        var vp = scrollRect.viewport ? scrollRect.viewport : (RectTransform)scrollRect.transform;
        float viewHalf = vp.rect.width * 0.5f;

        for (int i = 0; i < content.childCount; i++)
        {
            var child = content.GetChild(i) as RectTransform;
            if (!child) continue;
            var pi = child.GetComponent<ProfileIconItem>();
            if (pi == null || pi.Id != id) continue;

            float childCenter = child.anchoredPosition.x + child.rect.width * 0.5f;
            Vector2 pos = content.anchoredPosition;
            pos.x = -childCenter + viewHalf;
            content.anchoredPosition = pos;
            return;
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