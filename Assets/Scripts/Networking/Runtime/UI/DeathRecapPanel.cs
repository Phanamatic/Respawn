using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Services;

namespace Game.Net
{
    [DisallowMultipleComponent]
    public sealed class DeathRecapPanel : MonoBehaviour
    {
        [Header("Assign in Inspector")]
        [SerializeField] CanvasGroup canvasGroup;
        [SerializeField] TMP_Text killerNameLabel;
        [SerializeField] Image killerIconImage;
        [SerializeField] TMP_Text damageValueLabel;
        [SerializeField] TMP_Text primaryLabel;
        [SerializeField] TMP_Text secondaryLabel;
        [SerializeField] TMP_Text meleeLabel;
        [SerializeField] TMP_Text throwableLabel;
        [SerializeField] string meleeDisplayName = "Knife";
        [SerializeField] string damagePrefix = "Damage: ";
        [SerializeField] string primaryPrefix = "Primary: ";
        [SerializeField] string secondaryPrefix = "Secondary: ";
        [SerializeField] string meleePrefix = "Melee: ";
        [SerializeField] string throwablePrefix = "Throwable: ";
        [SerializeField] Sprite fallbackIcon;

    PlayerNetwork _owner;
    bool _visible;
    Coroutine _bindRoutine;

        void Awake()
        {
            canvasGroup ??= GetComponent<CanvasGroup>();
            HideImmediate();
            if (!fallbackIcon)
                fallbackIcon = ProfileIconLookup.Resolve(null);
        }

        void OnEnable()
        {
            if (_bindRoutine == null)
                _bindRoutine = StartCoroutine(CoBindOwner());
        }

        void OnDisable()
        {
            if (_bindRoutine != null)
            {
                StopCoroutine(_bindRoutine);
                _bindRoutine = null;
            }
        }

        void OnDestroy()
        {
            if (_owner != null)
                PlayerNetwork.RemoveDeathRecapCallbacks(ShowRecap, HideRecap);
            _owner = null;
        }

        public void Bind(PlayerNetwork owner)
        {
            if (_owner == owner) return;
            if (_owner != null)
                PlayerNetwork.RemoveDeathRecapCallbacks(ShowRecap, HideRecap);
            _owner = owner;
            if (_owner != null)
                PlayerNetwork.InstallDeathRecapCallbacks(ShowRecap, HideRecap);
        }

        IEnumerator CoBindOwner()
        {
            while (true)
            {
                var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < players.Length; i++)
                {
                    if (players[i] && players[i].IsOwner)
                    {
                        Bind(players[i]);
                        _bindRoutine = null;
                        yield break;
                    }
                }
                yield return null;
            }
        }

        internal void ShowRecap(PlayerNetwork.DeathRecapPayload payload)
        {
            ApplyPayload(payload);
            SetVisible(true);
        }

        internal void HideRecap()
        {
            SetVisible(false);
        }

        void ApplyPayload(PlayerNetwork.DeathRecapPayload payload)
        {
            var killerName = payload.killerName.IsEmpty ? "Unknown" : payload.killerName.ToString();
            if (killerNameLabel) killerNameLabel.text = killerName;

            var iconId = payload.killerIconId.IsEmpty ? null : payload.killerIconId.ToString();
            if (killerIconImage)
            {
                var sprite = !string.IsNullOrWhiteSpace(iconId) ? ProfileIconLookup.Resolve(iconId) : null;
                if (!sprite) sprite = fallbackIcon;
                killerIconImage.sprite = sprite;
                killerIconImage.enabled = sprite != null;
            }

            if (damageValueLabel)
            {
                int dmgInt = Mathf.RoundToInt(Mathf.Max(0f, payload.damage));
                damageValueLabel.text = $"{damagePrefix}{dmgInt}";
            }

            if (primaryLabel)
            {
                var primary = (PrimaryType)payload.primary;
                primaryLabel.text = $"{primaryPrefix}{FormatEnum(primary)}";
            }

            if (secondaryLabel)
            {
                var secondary = (SecondaryType)payload.secondary;
                secondaryLabel.text = $"{secondaryPrefix}{FormatEnum(secondary)}";
            }

            if (meleeLabel)
            {
                meleeLabel.text = $"{meleePrefix}{meleeDisplayName}";
            }

            if (throwableLabel)
            {
                var utility = (UtilityType)payload.utility;
                throwableLabel.text = $"{throwablePrefix}{FormatEnum(utility)}";
            }
        }

        static string FormatEnum<T>(T value) where T : struct, System.Enum
        {
            var text = value.ToString();
            return string.IsNullOrEmpty(text) ? "None" : text.Replace('_', ' ');
        }

        void SetVisible(bool visible)
        {
            _visible = visible;
            if (canvasGroup)
            {
                canvasGroup.alpha = visible ? 1f : 0f;
                canvasGroup.interactable = visible;
                canvasGroup.blocksRaycasts = visible;
            }
            else
            {
                gameObject.SetActive(visible);
            }
        }

        void HideImmediate()
        {
            SetVisible(false);
        }

    }
}
