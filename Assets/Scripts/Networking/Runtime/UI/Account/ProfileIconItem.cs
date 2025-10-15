using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Account
{
    /// Button+Image item for profile icon selection.
    public sealed class ProfileIconItem : MonoBehaviour
    {
        [SerializeField] Button button;
        [SerializeField] Image  image;

        [Header("Selection Frame")]
        [SerializeField] RectTransform framePanel;   // panel behind the button/image
        [SerializeField] Vector2 borderPadding = new Vector2(6f, 6f); // always bigger than button
        [SerializeField] bool matchImageScaleAndSize = true;  // keep frame same scale/size as image
        [SerializeField, Min(1f)] float selectedScale = 1.08f; // extra pop when selected
        [SerializeField] float scaleLerp = 18f;      // smoothness

        public string Id { get; private set; }
        Action<string, Sprite> _onClick;
        bool _selected;
        Vector3 _baseScale = Vector3.one;

        void Awake()
        {
            if (matchImageScaleAndSize && image)
            {
                _baseScale = image.rectTransform.localScale;
                if (framePanel) framePanel.localScale = _baseScale;
            }
            else if (framePanel) _baseScale = framePanel.localScale;

            ApplyFramePadding();
        }

        public void Bind(string id, Sprite sprite, Action<string, Sprite> onClick)
        {
            Id = id;
            _onClick = onClick;
            if (image && sprite) image.sprite = sprite;

            if (matchImageScaleAndSize && image && framePanel)
            {
                _baseScale = image.rectTransform.localScale;
                framePanel.localScale = _baseScale;
            }

            ApplyFramePadding();

            if (button)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => _onClick?.Invoke(Id, image ? image.sprite : null));
            }
            SetSelected(false, instant:true);
        }

        public void SetSelected(bool on, bool instant = false)
        {
            _selected = on;
            if (!framePanel) return;

            var target = _baseScale * (_selected ? selectedScale : 1f);
            if (matchImageScaleAndSize && image)
            {
                target = image.rectTransform.localScale * (_selected ? selectedScale : 1f);
            }
            if (instant) framePanel.localScale = target;
            else framePanel.localScale = Vector3.Lerp(framePanel.localScale, target, 1f); // snap once; LateUpdate smooths
        }

        void LateUpdate()
        {
            if (!framePanel) return;
            var target = _baseScale * (_selected ? selectedScale : 1f);
            if (matchImageScaleAndSize && image)
            {
                target = image.rectTransform.localScale * (_selected ? selectedScale : 1f);
            }
            float t = 1f - Mathf.Exp(-scaleLerp * Time.unscaledDeltaTime);
            framePanel.localScale = Vector3.Lerp(framePanel.localScale, target, t);
        }

        /// Keep the frame slightly larger than the button image to form a border.
        void ApplyFramePadding()
        {
            if (!framePanel || !image) return;
            var imgRT = image.rectTransform;

            framePanel.anchorMin = imgRT.anchorMin;
            framePanel.anchorMax = imgRT.anchorMax;
            framePanel.pivot     = imgRT.pivot;
            framePanel.anchoredPosition = imgRT.anchoredPosition;

            // Expand around the image
            var padding = borderPadding;
            if (matchImageScaleAndSize)
            {
                var scale = imgRT.localScale;
                padding *= Mathf.Max(scale.x, scale.y);
            }
            framePanel.offsetMin = new Vector2(-padding.x, -padding.y);
            framePanel.offsetMax = new Vector2( padding.x,  padding.y);

            framePanel.SetAsFirstSibling(); // render behind the button
        }

        void OnRectTransformDimensionsChange()
        {
            ApplyFramePadding(); // maintain border on layout changes
        }
    }
}
// Lightweight item. Assign in prefab and instantiate under ScrollView Content.
