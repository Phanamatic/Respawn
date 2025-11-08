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
        Vector3 _primaryReloadBaseScale = Vector3.one, _secondaryReloadBaseScale = Vector3.one;
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
            
            // Show UI if weapon name is set OR if we have ammo (handles replication delay)
            bool showUI = equipped || hasAmmo;
            
            if (primaryWeaponName)
            {
                primaryWeaponName.enabled = showUI;
                if (equipped) primaryWeaponName.text = _primary.equippedWeaponName.Value.ToString();
            }
            if (primaryAmmoText)
            {
                primaryAmmoText.enabled = showUI;
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    primaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                    
                    // Contextual tint when low/critical
                    var normal = _primaryAmmoBaseColor;
                    primaryAmmoText.color = ChooseAmmoColor(magazine, normal, ammoLowColor, ammoCriticalColor);
                }
            }
            
            if (primaryReloadFill)
            {
                bool reloading = _primary.isReloading.Value;
                float progress = reloading ? _primary.reloadProgress.Value : 0f;
                
                primaryReloadFill.fillAmount = progress;
                primaryReloadFill.enabled = reloading;
                
                // Pulse the circle near completion to telegraph the end of reload.
                bool nearDone = reloading && progress >= reloadPulseThreshold;
                ApplyReloadPulse(primaryReloadFill, _primaryReloadBaseScale, reloading, nearDone);
            }
        }

        void UpdateSecondaryUI()
        {
            if (!_secondary) return;

            bool equipped = _secondary.equippedWeaponName.Value.Length > 0;
            int magazine = Mathf.Max(0, _secondary.magazineAmmo.Value);
            int reserve = _secondary.reserveAmmo.Value;
            bool hasAmmo = magazine > 0 || reserve != 0;
            
            // Show UI if weapon name is set OR if we have ammo (handles replication delay)
            bool showUI = equipped || hasAmmo;
            
            if (secondaryWeaponName)
            {
                secondaryWeaponName.enabled = showUI;
                if (equipped) secondaryWeaponName.text = _secondary.equippedWeaponName.Value.ToString();
            }
            if (secondaryAmmoText)
            {
                secondaryAmmoText.enabled = showUI;
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    secondaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                    
                    // Contextual tint when low/critical
                    var normal = _secondaryAmmoBaseColor;
                    secondaryAmmoText.color = ChooseAmmoColor(magazine, normal, ammoLowColor, ammoCriticalColor);
                }
            }
            
            if (secondaryReloadFill)
            {
                bool reloading = _secondary.isReloading.Value;
                float progress = reloading ? _secondary.reloadProgress.Value : 0f;
                
                secondaryReloadFill.fillAmount = progress;
                secondaryReloadFill.enabled = reloading;
                
                // Pulse the circle near completion to telegraph the end of reload.
                bool nearDone = reloading && progress >= reloadPulseThreshold;
                ApplyReloadPulse(secondaryReloadFill, _secondaryReloadBaseScale, reloading, nearDone);
            }
        }

        void UpdateMeleeUI()
        {
            if (!_melee) return;

            bool equipped = _melee.equippedWeaponName.Value.Length > 0;
            if (meleeWeaponName)
            {
                meleeWeaponName.enabled = equipped;
                if (equipped) meleeWeaponName.text = _melee.equippedWeaponName.Value.ToString();
            }
        }

        void UpdateUtilityUI()
        {
            if (!_utility) return;

            bool equipped = _utility.equippedWeaponName.Value.Length > 0;
            if (utilityWeaponName)
            {
                utilityWeaponName.enabled = equipped;
                if (equipped) utilityWeaponName.text = _utility.equippedWeaponName.Value.ToString();
            }
            if (utilityAmmoText)
            {
                utilityAmmoText.enabled = equipped;
                if (equipped) utilityAmmoText.text = $"x{_utility.ammoCount.Value}";
            }
        }

        void HideAll()
        {
            if (primaryReloadFill) primaryReloadFill.enabled = false;
            if (primaryAmmoText) { primaryAmmoText.enabled = false; primaryAmmoText.text = ""; }
            if (primaryWeaponName) { primaryWeaponName.enabled = false; primaryWeaponName.text = ""; }

            if (secondaryReloadFill) secondaryReloadFill.enabled = false;
            if (secondaryAmmoText) { secondaryAmmoText.enabled = false; secondaryAmmoText.text = ""; }
            if (secondaryWeaponName) { secondaryWeaponName.enabled = false; secondaryWeaponName.text = ""; }

            if (meleeWeaponName) { meleeWeaponName.enabled = false; meleeWeaponName.text = ""; }

            if (utilityAmmoText) { utilityAmmoText.enabled = false; utilityAmmoText.text = ""; }
            if (utilityWeaponName) { utilityWeaponName.enabled = false; utilityWeaponName.text = ""; }
        }
// Hides equip texts unless equipped; reload image only when reloading.

        // ===== Helpers: defaults, tint logic, pulse animation =====
        void CacheDefaults()
        {
            if (primaryAmmoText) _primaryAmmoBaseColor = primaryAmmoText.color;
            if (secondaryAmmoText) _secondaryAmmoBaseColor = secondaryAmmoText.color;

            if (primaryReloadFill) _primaryReloadBaseScale = primaryReloadFill.rectTransform.localScale;
            if (secondaryReloadFill) _secondaryReloadBaseScale = secondaryReloadFill.rectTransform.localScale;
        }

        Color ChooseAmmoColor(int magazine, Color normal, Color low, Color critical)
        {
            if (magazine <= Mathf.Max(0, criticalAmmoThreshold)) return critical;
            if (magazine <= Mathf.Max(criticalAmmoThreshold + 1, lowAmmoThreshold)) return low;
            return normal;
        }

        void ApplyReloadPulse(Image img, Vector3 baseScale, bool enabled, bool shouldPulse)
        {
            if (!img) return;

            // When the image is disabled or no pulse requested, ensure we restore the base scale.
            if (!enabled || !shouldPulse)
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
    }
}