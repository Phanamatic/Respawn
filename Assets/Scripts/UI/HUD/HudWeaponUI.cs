using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Game.Net.Weapons;

namespace Game.UI.HUD
{
    /// Attach to a HUD canvas. Assign fields in Inspector.
    public sealed class HudWeaponUI : MonoBehaviour
    {
        [Header("Primary Weapon")]
        // === Contextual cues ===
        [Header("Ammo/Reload Cues")]
        [Tooltip("Tint ammo text when magazine is at or below these thresholds.")]
        [SerializeField, Range(0, 64)] int lowAmmoThreshold = 6;
        [SerializeField, Range(0, 64)] int criticalAmmoThreshold = 2;

        [SerializeField] Color ammoLowColor = new Color(1f, 0.85f, 0.3f, 1f);
        [SerializeField] Color ammoCriticalColor = new Color(1f, 0.35f, 0.35f, 1f);

        [Tooltip("When reloadProgress >= threshold, pulse the reload fill to telegraph completion.")]
        [SerializeField, Range(0f, 1f)] float reloadPulseThreshold = 0.85f;
        [SerializeField, Range(0f, 0.3f)] float pulseScale = 0.08f;
        [SerializeField, Range(0f, 20f)] float pulseSpeed = 10f;

        // cached defaults so we can cleanly restore when cues stop
        Color _primaryAmmoBaseColor, _secondaryAmmoBaseColor;
        Color _primaryReloadBaseColor, _secondaryReloadBaseColor;
        Vector3 _primaryReloadBaseScale = Vector3.one, _secondaryReloadBaseScale = Vector3.one;
// Keep UI components always enabled; we control visibility via alpha, never by disabling components.
// Adds serialized knobs and caches for tint/pulse behavior.

        [SerializeField] Image primaryReloadFill;     // type = Filled
        [SerializeField] TMP_Text primaryAmmoText;
        [SerializeField] TMP_Text primaryWeaponName;

        [Header("Secondary Weapon")]
        [SerializeField] Image secondaryReloadFill;
        [SerializeField] TMP_Text secondaryAmmoText;
        [SerializeField] TMP_Text secondaryWeaponName;

        [Header("Melee Weapon")]
        [SerializeField] TMP_Text meleeWeaponName;

        [Header("Utility Weapon")]
        [SerializeField] TMP_Text utilityAmmoText;
        [SerializeField] TMP_Text utilityWeaponName;

        WeaponPrimaryController _primary;
        WeaponSecondaryController _secondary;
        WeaponMeleeController _melee;
        WeaponUtilityController _utility;

        void Start()
        {
            CacheDefaults();     // capture base colors/scales before we hide
            HideAll();
            FindControllers();
        }

        void Update()
        {
            if (!_primary) { FindControllers(); return; }

            UpdatePrimaryUI();
            UpdateSecondaryUI();
            UpdateMeleeUI();
            UpdateUtilityUI();
        }

        void UpdatePrimaryUI()
        {
            if (!_primary) return;

            bool equipped = _primary.equippedWeaponName.Value.Length > 0;
            int magazine = Mathf.Max(0, _primary.magazineAmmo.Value);
            int reserve = _primary.reserveAmmo.Value;
            bool hasAmmo = magazine > 0 || reserve != 0;
            
            // Visible when we have a name or ammo info (handles replication delay).
            bool showUI = equipped || hasAmmo;
            
            if (primaryWeaponName)
            {
                if (equipped) primaryWeaponName.text = _primary.equippedWeaponName.Value.ToString();
                SetAlpha(primaryWeaponName, showUI ? 1f : 0f);
            }
            if (primaryAmmoText)
            {
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    primaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                    
                    // Contextual tint when low/critical
                    var normal = _primaryAmmoBaseColor;
                    primaryAmmoText.color = ChooseAmmoColor(magazine, normal, ammoLowColor, ammoCriticalColor);
                }
                else
                {
                    primaryAmmoText.text = "";
                    // restore base color to avoid lingering low/critical tint when we fade back in
                    primaryAmmoText.color = _primaryAmmoBaseColor;
                }
                SetAlpha(primaryAmmoText, showUI ? 1f : 0f);
            }
            
            if (primaryReloadFill)
            {
                bool reloading = _primary.isReloading.Value;
                float progress = reloading ? _primary.reloadProgress.Value : 0f;
                
                primaryReloadFill.fillAmount = progress;
                SetAlpha(primaryReloadFill, reloading ? 1f : 0f);
                
                // Pulse the circle near completion to telegraph the end of reload.
                bool nearDone = reloading && progress >= reloadPulseThreshold;
                ApplyReloadPulse(primaryReloadFill, _primaryReloadBaseScale, reloading, nearDone);
                
                if (!reloading)
                {
                    // restore base color/scale for next reload
                    primaryReloadFill.color = new Color(_primaryReloadBaseColor.r, _primaryReloadBaseColor.g, _primaryReloadBaseColor.b, primaryReloadFill.color.a);
                }
            }
        }

        void UpdateSecondaryUI()
        {
            if (!_secondary) return;

            bool equipped = _secondary.equippedWeaponName.Value.Length > 0;
            int magazine = Mathf.Max(0, _secondary.magazineAmmo.Value);
            int reserve = _secondary.reserveAmmo.Value;
            bool hasAmmo = magazine > 0 || reserve != 0;
            
            bool showUI = equipped || hasAmmo;
            
            if (secondaryWeaponName)
            {
                if (equipped) secondaryWeaponName.text = _secondary.equippedWeaponName.Value.ToString();
                SetAlpha(secondaryWeaponName, showUI ? 1f : 0f);
            }
            if (secondaryAmmoText)
            {
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    secondaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                    var normal = _secondaryAmmoBaseColor;
                    secondaryAmmoText.color = ChooseAmmoColor(magazine, normal, ammoLowColor, ammoCriticalColor);
                }
                else
                {
                    secondaryAmmoText.text = "";
                    secondaryAmmoText.color = _secondaryAmmoBaseColor;
                }
                SetAlpha(secondaryAmmoText, showUI ? 1f : 0f);
            }
            
            if (secondaryReloadFill)
            {
                bool reloading = _secondary.isReloading.Value;
                float progress = reloading ? _secondary.reloadProgress.Value : 0f;
                
                secondaryReloadFill.fillAmount = progress;
                SetAlpha(secondaryReloadFill, reloading ? 1f : 0f);
                
                bool nearDone = reloading && progress >= reloadPulseThreshold;
                ApplyReloadPulse(secondaryReloadFill, _secondaryReloadBaseScale, reloading, nearDone);
                
                if (!reloading)
                {
                    secondaryReloadFill.color = new Color(_secondaryReloadBaseColor.r, _secondaryReloadBaseColor.g, _secondaryReloadBaseColor.b, secondaryReloadFill.color.a);
                }
            }
        }

        void UpdateMeleeUI()
        {
            if (!_melee) return;

            bool equipped = _melee.equippedWeaponName.Value.Length > 0;
            if (meleeWeaponName)
            {
                if (equipped) meleeWeaponName.text = _melee.equippedWeaponName.Value.ToString();
                else meleeWeaponName.text = "";
                SetAlpha(meleeWeaponName, equipped ? 1f : 0f);
            }
        }

        void UpdateUtilityUI()
        {
            if (!_utility) return;

            bool equipped = _utility.equippedWeaponName.Value.Length > 0;
            if (utilityWeaponName)
            {
                if (equipped) utilityWeaponName.text = _utility.equippedWeaponName.Value.ToString();
                else utilityWeaponName.text = "";
                SetAlpha(utilityWeaponName, equipped ? 1f : 0f);
            }
            if (utilityAmmoText)
            {
                if (equipped) utilityAmmoText.text = $"x{_utility.ammoCount.Value}";
                else utilityAmmoText.text = "";
                SetAlpha(utilityAmmoText, equipped ? 1f : 0f);
            }
        }

        void HideAll()
        {
            // Never disable UI components. Clear content and fade out via alpha.
            if (primaryReloadFill) { primaryReloadFill.fillAmount = 0f; SetAlpha(primaryReloadFill, 0f); }
            if (primaryAmmoText)   { primaryAmmoText.text = "";         SetAlpha(primaryAmmoText,   0f); }
            if (primaryWeaponName) { primaryWeaponName.text = "";       SetAlpha(primaryWeaponName, 0f); }

            if (secondaryReloadFill) { secondaryReloadFill.fillAmount = 0f; SetAlpha(secondaryReloadFill, 0f); }
            if (secondaryAmmoText)   { secondaryAmmoText.text = "";         SetAlpha(secondaryAmmoText,   0f); }
            if (secondaryWeaponName) { secondaryWeaponName.text = "";       SetAlpha(secondaryWeaponName, 0f); }

            if (meleeWeaponName)   { meleeWeaponName.text = "";       SetAlpha(meleeWeaponName,   0f); }
            if (utilityAmmoText)   { utilityAmmoText.text = "";       SetAlpha(utilityAmmoText,   0f); }
            if (utilityWeaponName) { utilityWeaponName.text = "";     SetAlpha(utilityWeaponName, 0f); }
        }
// Components stay enabled; alpha=0 hides them without layout/shader side effects.

        // ===== Helpers: defaults, tint logic, pulse animation =====
        void CacheDefaults()
        {
            if (primaryAmmoText) _primaryAmmoBaseColor = primaryAmmoText.color;
            if (secondaryAmmoText) _secondaryAmmoBaseColor = secondaryAmmoText.color;

            if (primaryReloadFill)
            {
                _primaryReloadBaseScale = primaryReloadFill.rectTransform.localScale;
                _primaryReloadBaseColor = primaryReloadFill.color;
            }
            if (secondaryReloadFill)
            {
                _secondaryReloadBaseScale = secondaryReloadFill.rectTransform.localScale;
                _secondaryReloadBaseColor = secondaryReloadFill.color;
            }
        }

        Color ChooseAmmoColor(int magazine, Color normal, Color low, Color critical)
        {
            if (magazine <= Mathf.Max(0, criticalAmmoThreshold)) return critical;
            if (magazine <= Mathf.Max(criticalAmmoThreshold + 1, lowAmmoThreshold)) return low;
            return normal;
        }

        void ApplyReloadPulse(Image img, Vector3 baseScale, bool visible, bool shouldPulse)
        {
            if (!img) return;

            // When not visible or no pulse requested, ensure we restore the base scale.
            if (!visible || !shouldPulse)
            {
                if (img.rectTransform.localScale != baseScale)
                    img.rectTransform.localScale = baseScale;
                return;
            }

            // Unscaled time so pause/slow-mo doesn't kill the cue.
            float s = 1f + pulseScale * Mathf.Abs(Mathf.Sin(Time.unscaledTime * pulseSpeed));
            img.rectTransform.localScale = baseScale * s;
        }

        void FindControllers()
        {
            var nm = NetworkManager.Singleton;
            if (!nm) return;
            var local = nm.LocalClient?.PlayerObject;
            if (!local) return;
            _primary = local.GetComponent<WeaponPrimaryController>();
            _secondary = local.GetComponent<WeaponSecondaryController>();
            _melee = local.GetComponent<WeaponMeleeController>();
            _utility = local.GetComponent<WeaponUtilityController>();
        }

        // ===== Helpers: alpha without disabling components =====
        static void SetAlpha(TMP_Text t, float a)
        {
            if (!t) return;
            var c = t.color; c.a = a; t.color = c;
        }
        static void SetAlpha(Image img, float a)
        {
            if (!img) return;
            var c = img.color; c.a = a; img.color = c;
        }
        // Never disables any Image/TMP components; uses alpha-only visibility with state restoration. Keeps HUD always alive and layout stable.
    }
}