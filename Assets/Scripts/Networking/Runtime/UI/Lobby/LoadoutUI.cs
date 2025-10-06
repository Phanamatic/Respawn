using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
// Ensure we reference the same types
using static Game.Net.UtilityType;
using static Game.Net.PrimaryType;
using static Game.Net.SecondaryType;
using TMPro;

namespace Game.Net
{
    // Attach to your Armoury panel root in the Lobby scene.
    public sealed class LoadoutUI : MonoBehaviour
    {
        [Header("Images (live preview)")]
        [SerializeField] Image primaryImage;
        [SerializeField] Image secondaryImage;
        [SerializeField] Image meleeImage;
        [SerializeField] Image utilityImage;

        [Header("Catalog (drag all WeaponData assets here)")]
        [SerializeField] UI.Scripts.WeaponData[] catalog;

        [Header("Scroll Grid")]
        [SerializeField] Transform listRoot;                   // ScrollView Content
        [SerializeField] GridLayoutGroup grid;                // Add to Content object

        [Tooltip("Prefab for Primary entries")]
        [SerializeField] LoadoutItemView primaryItemPrefab;

        [Tooltip("Prefab for Secondary entries")]
        [SerializeField] LoadoutItemView secondaryItemPrefab;

        [Tooltip("Prefab used for Melee entries (different image size)")]
        [SerializeField] LoadoutItemView meleeItemPrefab;

        [Tooltip("Prefab used for Utility entries (different image size)")]
        [SerializeField] LoadoutItemView utilityItemPrefab;

        [Header("Category Tabs")]
        [SerializeField] Button primaryTabBtn;
        [SerializeField] Button secondaryTabBtn;
        [SerializeField] Button meleeTabBtn;
        [SerializeField] Button utilityTabBtn;

        [Header("Sprites (fallback if a catalog sprite missing)")]
        [SerializeField] Sprite knifeSprite;

        [Header("Save/Discard")]
        [SerializeField] Button saveBtn, discardBtn;

        [Header("Notifications")]
        [SerializeField] RectTransform notificationPanel;   // assign in Inspector
        [SerializeField] TMP_Text notificationText;         // assign in Inspector
        [SerializeField, Range(0.5f, 5f)] float notificationDuration = 2.2f;
        [SerializeField, Range(0.05f, 1f)] float notificationAnimTime = 0.2f;
        [SerializeField] Vector2 notificationHiddenOffset = new Vector2(0f, -160f);

        // runtime
        Vector2 _notifHome;
        bool _notifSetup;
        Coroutine _notifRoutine;
        Coroutine _savingRoutine;

        PlayerLoadout _saved = PlayerLoadout.Default;
        PlayerLoadout _working = PlayerLoadout.Default;
        bool _loaded;

        [Header("Scroll")]
        [SerializeField, Range(1f, 400f)] float scrollSpeed = 140f; // faster scroll

        [Header("Grid Padding")]
        [SerializeField] int paddingLeft = 12;
        [SerializeField] int paddingTop  = 12;

        [Header("Layout")]
        [SerializeField, Min(1)] int columnCount = 2;   // force two columns

        // Active tab drives equip routing.
        UI.Scripts.WeaponCategory _currentCategory = UI.Scripts.WeaponCategory.Primary;

        // Derive Utility type when authoring missed enum.
        UtilityType CoerceUtility(UI.Scripts.WeaponData w)
        {
            if (w.utilityType != UtilityType.None) return w.utilityType;
            var n = (w.weaponName ?? string.Empty).ToLowerInvariant();
            if (n.Contains("smoke")) return UtilityType.Smoke;
            if (n.Contains("stun"))  return UtilityType.Stun;
            if (n.Contains("gren"))  return UtilityType.Grenade;
            return UtilityType.Grenade;
        }

        void OnEnable()
        {
            HookUI(true);
        }
        void OnDisable()
        {
            HookUI(false);
        }

        // Ensure grid exists and is configured when the view comes alive.
        void Start()
        {
            EnsureGrid();
            EnsureNotificationSetup();
            Opened();
        }

        void EnsureGrid()
        {
            if (!listRoot) return;

            if (!grid)
            {
                grid = listRoot.GetComponent<GridLayoutGroup>();
                if (!grid) grid = listRoot.gameObject.AddComponent<GridLayoutGroup>();
            }

            // Content fits children and stays aligned.
            var fitter = listRoot.GetComponent<ContentSizeFitter>();
            if (!fitter) fitter = listRoot.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            // Faster scroll.
            var sr = listRoot.GetComponentInParent<ScrollRect>();
            if (sr) sr.scrollSensitivity = scrollSpeed;
        }

        void HookUI(bool on)
        {
            if (on)
            {
                if (primaryTabBtn)   primaryTabBtn.onClick.AddListener(() => ShowCategory(UI.Scripts.WeaponCategory.Primary));
                if (secondaryTabBtn) secondaryTabBtn.onClick.AddListener(() => ShowCategory(UI.Scripts.WeaponCategory.Secondary));
                if (meleeTabBtn)     meleeTabBtn.onClick.AddListener(() => ShowCategory(UI.Scripts.WeaponCategory.Melee));
                if (utilityTabBtn)   utilityTabBtn.onClick.AddListener(() => ShowCategory(UI.Scripts.WeaponCategory.Utility));

                if (saveBtn)    saveBtn.onClick.AddListener(OnSaveClicked);
                if (discardBtn) discardBtn.onClick.AddListener(OnDiscardClicked);
            }
            else
            {
                if (primaryTabBtn)   primaryTabBtn.onClick.RemoveAllListeners();
                if (secondaryTabBtn) secondaryTabBtn.onClick.RemoveAllListeners();
                if (meleeTabBtn)     meleeTabBtn.onClick.RemoveAllListeners();
                if (utilityTabBtn)   utilityTabBtn.onClick.RemoveAllListeners();

                if (saveBtn)    saveBtn.onClick.RemoveAllListeners();
                if (discardBtn) discardBtn.onClick.RemoveAllListeners();
            }
        }

        public async void Opened()
        {
            if (!_loaded)
                await LoadFromCloud();

            RefreshImages();
            // Default to Primary list on open
            ShowCategory(UI.Scripts.WeaponCategory.Primary);
        }

        async Task LoadFromCloud()
        {
            _saved = await CloudSaveClient.LoadLoadoutAsync(PlayerLoadout.Default);
            // sanitize old saves if any (values outside enum range)
            if ((byte)_saved.Utility > (byte)UtilityType.Stun) _saved.Utility = UtilityType.Grenade;
            _working = _saved;
            _loaded = true;
            RefreshImages();
            SessionContext.SetLoadout(_saved);
        }

        async void OnSaveClicked()
        {
            StartSavingNotification();
            var ok = await CloudSaveClient.SaveLoadoutAsync(_working);
            StopSavingNotification(ok);
            if (ok)
            {
                _saved = _working;
                SessionContext.SetLoadout(_saved);
            }
        }

        async void OnDiscardClicked()
        {
            _working = _saved;
            RefreshImages();
            Notify("Loadout changes discarded!");
            await Task.Yield();
        }

        void RefreshImages()
        {
            if (primaryImage)
            {
                var s = SpriteForPrimary(_working.Primary);
                if (s) primaryImage.sprite = s;
            }

            if (secondaryImage)
            {
                var s = SpriteForSecondary(_working.Secondary);
                if (s) secondaryImage.sprite = s;
            }

            if (meleeImage && knifeSprite) meleeImage.sprite = knifeSprite;

            if (utilityImage)
            {
                var s = SpriteForUtility(_working.Utility);
                if (s) utilityImage.sprite = s;
            }
        }

        // Build the scroll list for a category
        void ShowCategory(UI.Scripts.WeaponCategory category)
        {
            if (!listRoot)
            {
                Debug.LogWarning("[LoadoutUI] listRoot not assigned.");
                return;
            }
            EnsureGrid();
            ClearList();

            _currentCategory = category; // route equipping by visible tab

            // Pick prefab per category
            LoadoutItemView prefab = null;
            switch (category)
            {
                case UI.Scripts.WeaponCategory.Primary:   prefab = primaryItemPrefab;   break;
                case UI.Scripts.WeaponCategory.Secondary: prefab = secondaryItemPrefab; break;
                case UI.Scripts.WeaponCategory.Melee:     prefab = meleeItemPrefab;     break;
                case UI.Scripts.WeaponCategory.Utility:   prefab = utilityItemPrefab;   break;
            }
            if (!prefab)
            {
                Debug.LogWarning($"[LoadoutUI] Prefab missing for {category}.");
                return;
            }

            SetupGridForPrefab(prefab);

            if (catalog == null || catalog.Length == 0)
            {
                Debug.LogWarning("[LoadoutUI] Catalog is empty. Assign WeaponData assets.");
                return;
            }

            int created = 0;

            // Build filtered list first.
            var items = new System.Collections.Generic.List<UI.Scripts.WeaponData>(catalog.Length);
            for (int i = 0; i < catalog.Length; i++)
            {
                var w = catalog[i];
                if (!w) continue;

                if (category == UI.Scripts.WeaponCategory.Utility)
                {
                    // Show all Utility-category assets OR anything with a utility type set.
                    if (!(w.category == UI.Scripts.WeaponCategory.Utility || w.utilityType != UtilityType.None)) continue;
                }
                else if (w.category != category) continue;

                items.Add(w);
            }

            // Custom order for Primary: AR, Shotgun, SMG, LMG, Sniper
            if (category == UI.Scripts.WeaponCategory.Primary)
            {
                int Order(UI.Scripts.WeaponData w) => w.primaryType switch
                {
                    PrimaryType.AR     => 0,
                    PrimaryType.Shotgun=> 1,
                    PrimaryType.SMG    => 2,
                    PrimaryType.LMG    => 3,
                    PrimaryType.Sniper => 4,
                    _ => 99
                };
                items.Sort((a,b) => Order(a).CompareTo(Order(b)));
            }

            for (int i = 0; i < items.Count; i++)
            {
                var w = items[i];
                var rowGO = Instantiate(prefab.gameObject, listRoot, false);
                var row   = rowGO.GetComponent<LoadoutItemView>();
                row.Bind(w, OnEquipClicked);
                created++;
            }

#if UNITY_EDITOR
            if (created == 0) Debug.LogWarning($"[LoadoutUI] No weapons found for {category}.");
#endif
        }

        void ClearList()
        {
            if (!listRoot) return;
            for (int i = listRoot.childCount - 1; i >= 0; i--)
                Destroy(listRoot.GetChild(i).gameObject);
        }

        void SetupGridForPrefab(LoadoutItemView prefab)
        {
            if (!grid) return;

            // Ensure Content is top-left anchored and not offset.
            if (listRoot is RectTransform contentRt)
            {
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(0f, 1f);
                contentRt.pivot     = new Vector2(0f, 1f);
                contentRt.anchoredPosition = Vector2.zero;
            }

            // Hard values per request.
            grid.cellSize       = new Vector2(504f, 346f);
            grid.spacing        = new Vector2(45f, 27f);
            grid.padding        = new RectOffset(paddingLeft, 0, paddingTop, 0);
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.startCorner    = GridLayoutGroup.Corner.UpperLeft;

            // Force two columns.
            grid.startAxis      = GridLayoutGroup.Axis.Horizontal;
            grid.constraint     = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount= Mathf.Max(1, columnCount);

            // Make Content wide enough for the fixed columns to prevent wrapping to 1 column.
            if (listRoot is RectTransform rt)
            {
                float cols = grid.constraintCount;
                float w = grid.padding.left + grid.padding.right + (grid.cellSize.x * cols) + (grid.spacing.x * (cols - 1));
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);

                // Lock width so parent layouts don’t collapse to a single column.
                var le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
                le.minWidth = w;
                le.preferredWidth = w;
            }
        }

        void OnEquipClicked(UI.Scripts.WeaponData w)
        {
            // Drive equip by the active tab to avoid misrouted secondary-on-primary clicks.
            switch (_currentCategory)
            {
                case UI.Scripts.WeaponCategory.Primary:
                    _working.Primary = w.primaryType;
                    Notify($"Set Primary to {w.weaponName}");
                    break;
                case UI.Scripts.WeaponCategory.Secondary:
                    _working.Secondary = w.secondaryType;
                    Notify($"Set Secondary to {w.weaponName}");
                    break;
                case UI.Scripts.WeaponCategory.Melee:
                    Notify("Set Melee to Knife");
                    break; // fixed Knife
                case UI.Scripts.WeaponCategory.Utility:
                    _working.Utility = CoerceUtility(w);
                    Notify($"Set Utility to {w.weaponName}");
                    break;
            }
            RefreshImages();
        }

        Sprite SpriteForPrimary(PrimaryType p)
        {
            // Prefer catalog sprite
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Length; i++)
                    if (catalog[i] && catalog[i].category == UI.Scripts.WeaponCategory.Primary && catalog[i].primaryType == p)
                        return catalog[i].weaponIcon;
            }
            return null;
        }

        Sprite SpriteForSecondary(SecondaryType s)
        {
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Length; i++)
                    if (catalog[i] && catalog[i].category == UI.Scripts.WeaponCategory.Secondary && catalog[i].secondaryType == s)
                        return catalog[i].weaponIcon;
            }
            return null;
        }

        Sprite SpriteForUtility(UtilityType u)
        {
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Length; i++)
                    if (catalog[i] && catalog[i].category == UI.Scripts.WeaponCategory.Utility && catalog[i].utilityType == u)
                        return catalog[i].weaponIcon;
            }
            return null;
        }

        // ---------------- Notifications ----------------
        void EnsureNotificationSetup()
        {
            if (_notifSetup) return;
            if (!notificationPanel) return;
            _notifHome = notificationPanel.anchoredPosition;
            notificationPanel.anchoredPosition = _notifHome + notificationHiddenOffset;
            _notifSetup = true;
        }

        void Notify(string message, float? duration = null)
        {
            EnsureNotificationSetup();
            if (!notificationPanel || !notificationText) return;

            if (_savingRoutine != null) { StopCoroutine(_savingRoutine); _savingRoutine = null; }
            if (_notifRoutine != null) StopCoroutine(_notifRoutine);

            notificationText.text = message;
            _notifRoutine = StartCoroutine(ShowThenHide(duration ?? notificationDuration));
        }

        void StartSavingNotification()
        {
            EnsureNotificationSetup();
            if (!notificationPanel || !notificationText) return;

            if (_notifRoutine != null) StopCoroutine(_notifRoutine);
            notificationText.text = "Saving loadout";
            if (_savingRoutine != null) StopCoroutine(_savingRoutine);
            _savingRoutine = StartCoroutine(SavingDots());
            _notifRoutine = StartCoroutine(SlideIn());
        }

        void StopSavingNotification(bool success)
        {
            if (_savingRoutine != null) { StopCoroutine(_savingRoutine); _savingRoutine = null; }
            if (!notificationPanel || !notificationText) return;

            notificationText.text = success ? "Loadout saved!" : "Loadout save failed!";
            if (_notifRoutine != null) StopCoroutine(_notifRoutine);
            _notifRoutine = StartCoroutine(ShowThenHide(notificationDuration));
        }

        IEnumerator SavingDots()
        {
            int dots = 0;
            while (true)
            {
                dots = (dots + 1) % 4;
                notificationText.text = "Saving loadout" + new string('.', dots);
                yield return new WaitForSecondsRealtime(0.3f);
            }
        }

        IEnumerator ShowThenHide(float visibleSeconds)
        {
            yield return SlideIn();
            yield return new WaitForSecondsRealtime(visibleSeconds);
            yield return SlideOut();
        }

        IEnumerator SlideIn()
        {
            if (!notificationPanel) yield break;
            Vector2 from = _notifHome + notificationHiddenOffset;
            Vector2 to   = _notifHome;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / notificationAnimTime;
                notificationPanel.anchoredPosition = Vector2.LerpUnclamped(from, to, t);
                yield return null;
            }
            notificationPanel.anchoredPosition = to;
        }

        IEnumerator SlideOut()
        {
            if (!notificationPanel) yield break;
            Vector2 from = notificationPanel.anchoredPosition;
            Vector2 to   = _notifHome + notificationHiddenOffset;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / notificationAnimTime;
                notificationPanel.anchoredPosition = Vector2.LerpUnclamped(from, to, t);
                yield return null;
            }
            notificationPanel.anchoredPosition = to;
        }
    }
}