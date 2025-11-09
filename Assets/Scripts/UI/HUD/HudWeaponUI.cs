using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Game.Net;               // WeaponSlot, NetLoadout, enums
using Game.Net.Weapons;

namespace Game.UI.HUD
{
    /// Attach to a HUD canvas. Assign fields in Inspector.
    public sealed class HudWeaponUI : MonoBehaviour
    {
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

        [Header("Primary Weapon")]
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

        [Header("Equip Highlight (active slot)")]
        [SerializeField, Range(1.0f, 1.5f)] float equipScale = 1.15f;
        [SerializeField, Range(4f, 24f)] float equipLerpSpeed = 12f;
        [SerializeField] bool   equipBold = true;
        [SerializeField] Color  equipGlowColor = new Color(1f, 1f, 1f, 0.85f);
        [SerializeField, Range(0f, 1f)] float equipGlowWidth = 0.25f;

        // controllers + player
        WeaponPrimaryController _primary;
        WeaponSecondaryController _secondary;
        WeaponMeleeController _melee;
        WeaponUtilityController _utility;
        Game.Net.PlayerNetwork _player;

        // name text defaults (scale + outline)
        Vector3 _nameScalePrimary = Vector3.one, _nameScaleSecondary = Vector3.one, _nameScaleMelee = Vector3.one, _nameScaleUtility = Vector3.one;
        float _outlineBasePrimary, _outlineBaseSecondary, _outlineBaseMelee, _outlineBaseUtility;
        Color _outlineColorBasePrimary, _outlineColorBaseSecondary, _outlineColorBaseMelee, _outlineColorBaseUtility;

        void Start()
        {
            CacheDefaults();     // capture base colors/scales before we hide
            HideAll();
            FindControllers();

            // Force reload rings to the correct Image type (common reason for “no fill while reloading”).
            EnsureFilled(primaryReloadFill);
            EnsureFilled(secondaryReloadFill);

            // Ensure each label has its own TMP material instance so we can animate outline safely
            MakeMaterialInstance(primaryWeaponName,   ref _outlineBasePrimary,   ref _outlineColorBasePrimary);
            MakeMaterialInstance(secondaryWeaponName, ref _outlineBaseSecondary, ref _outlineColorBaseSecondary);
            MakeMaterialInstance(meleeWeaponName,     ref _outlineBaseMelee,     ref _outlineColorBaseMelee);
            MakeMaterialInstance(utilityWeaponName,   ref _outlineBaseUtility,   ref _outlineColorBaseUtility);

            _nameScalePrimary   = primaryWeaponName   != null ? primaryWeaponName.rectTransform.localScale   : Vector3.one;
            _nameScaleSecondary = secondaryWeaponName != null ? secondaryWeaponName.rectTransform.localScale : Vector3.one;
            _nameScaleMelee     = meleeWeaponName     != null ? meleeWeaponName.rectTransform.localScale     : Vector3.one;
            _nameScaleUtility   = utilityWeaponName   != null ? utilityWeaponName.rectTransform.localScale   : Vector3.one;
        }

        void Update()
        {
            if (_primary == null || _secondary == null || _melee == null || _utility == null || _player == null)
            {
                FindControllers();
            }

            // Keep existing per-slot HUD (ammo, reload rings, etc.)
            UpdatePrimaryUI();
            UpdateSecondaryUI();
            UpdateMeleeUI();
            UpdateUtilityUI();

            // Names from replicated loadout so labels show immediately on load
            EnsureNamesFromLoadout();

            // Animate selected slot label
            UpdateEquipHighlight();
        }

        void UpdatePrimaryUI()
        {
            if (_primary == null) return;

            bool equipped = _primary.equippedWeaponName.Value.Length > 0;
            int  magazine = Mathf.Max(0, (int)_primary.magazineAmmo.Value);
            int  reserve  = (int)_primary.reserveAmmo.Value;
            bool hasAmmo  = magazine > 0 || reserve != 0;
            bool showUI   = equipped || hasAmmo;

            if (primaryWeaponName != null)
            {
                if (equipped) primaryWeaponName.text = _primary.equippedWeaponName.Value.ToString();
                SetAlpha(primaryWeaponName, showUI ? 1f : 0f);
            }
            if (primaryAmmoText != null)
            {
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : string.Format("{0:D3}", Mathf.Max(0, reserve));
                    primaryAmmoText.text = string.Format("{0:D3}/{1}", Mathf.Max(0, magazine), reserveText);
                    var normal = _primaryAmmoBaseColor;
                    primaryAmmoText.color = ChooseAmmoColor(magazine, normal, ammoLowColor, ammoCriticalColor);
                }
                else
                {
                    primaryAmmoText.text = "";
                    primaryAmmoText.color = _primaryAmmoBaseColor;
                }
                SetAlpha(primaryAmmoText, showUI ? 1f : 0f);
            }

            if (primaryReloadFill != null)
            {
                bool  reloading = _primary.isReloading.Value;
                float progress  = reloading ? _primary.reloadProgress.Value : 0f;

                primaryReloadFill.fillAmount = Mathf.Clamp01(progress);
                SetAlpha(primaryReloadFill, reloading ? 1f : 0f);

                // Pulse near completion to telegraph end of reload
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
            if (_secondary == null) return;

            bool equipped = _secondary.equippedWeaponName.Value.Length > 0;
            int  magazine = Mathf.Max(0, (int)_secondary.magazineAmmo.Value);
            int  reserve  = (int)_secondary.reserveAmmo.Value;
            bool hasAmmo  = magazine > 0 || reserve != 0;
            bool showUI   = equipped || hasAmmo;

            if (secondaryWeaponName != null)
            {
                if (equipped) secondaryWeaponName.text = _secondary.equippedWeaponName.Value.ToString();
                SetAlpha(secondaryWeaponName, showUI ? 1f : 0f);
            }
            if (secondaryAmmoText != null)
            {
                if (showUI)
                {
                    string reserveText = reserve < 0 ? "INF" : string.Format("{0:D3}", Mathf.Max(0, reserve));
                    secondaryAmmoText.text = string.Format("{0:D3}/{1}", Mathf.Max(0, magazine), reserveText);
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

            if (secondaryReloadFill != null)
            {
                bool  reloading = _secondary.isReloading.Value;
                float progress  = reloading ? _secondary.reloadProgress.Value : 0f;

                secondaryReloadFill.fillAmount = Mathf.Clamp01(progress);
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
            if (_melee == null) return;

            bool equipped = _melee.equippedWeaponName.Value.Length > 0;
            if (meleeWeaponName != null)
            {
                if (equipped) meleeWeaponName.text = _melee.equippedWeaponName.Value.ToString();
                else meleeWeaponName.text = "";
                SetAlpha(meleeWeaponName, equipped ? 1f : 0f);
            }
        }

        void UpdateUtilityUI()
        {
            if (_utility == null) return;

            bool equipped = _utility.equippedWeaponName.Value.Length > 0;
            if (utilityWeaponName != null)
            {
                if (equipped) utilityWeaponName.text = _utility.equippedWeaponName.Value.ToString();
                else utilityWeaponName.text = "";
                SetAlpha(utilityWeaponName, equipped ? 1f : 0f);
            }
            if (utilityAmmoText != null)
            {
                if (equipped) utilityAmmoText.text = $"x{_utility.ammoCount.Value}";
                else utilityAmmoText.text = "";
                SetAlpha(utilityAmmoText, equipped ? 1f : 0f);
            }
        }

        // Names from replicated loadout so they appear as soon as loadout is known
        void EnsureNamesFromLoadout()
        {
            if (_player == null) return;

            var net = _player.GetCurrentNetLoadout(); // replicated NV (Everyone can read)

            bool valid = net.primary != (byte)PrimaryType.None
                      || net.secondary != (byte)SecondaryType.None
                      || net.util != (byte)UtilityType.None
                      || net.melee != 0;

            if (!valid) return;

            if (primaryWeaponName   != null) primaryWeaponName.text   = ((PrimaryType)net.primary).ToString();
            if (secondaryWeaponName != null) secondaryWeaponName.text = ((SecondaryType)net.secondary).ToString();
            if (meleeWeaponName     != null) meleeWeaponName.text     = "Knife";
            if (utilityWeaponName   != null) utilityWeaponName.text   = ((UtilityType)net.util).ToString();
        }

        // Highlight the equipped slot’s label
        void UpdateEquipHighlight()
        {
            if (_player == null) return;
            var slot = _player.GetActiveSlot();

            AnimateName(primaryWeaponName,   slot == WeaponSlot.Primary,   ref _nameScalePrimary,   _outlineBasePrimary,   _outlineColorBasePrimary);
            AnimateName(secondaryWeaponName, slot == WeaponSlot.Secondary, ref _nameScaleSecondary, _outlineBaseSecondary, _outlineColorBaseSecondary);
            AnimateName(meleeWeaponName,     slot == WeaponSlot.Melee,     ref _nameScaleMelee,     _outlineBaseMelee,     _outlineColorBaseMelee);
            AnimateName(utilityWeaponName,   slot == WeaponSlot.Utility,   ref _nameScaleUtility,   _outlineBaseUtility,   _outlineColorBaseUtility);
        }

        void AnimateName(TMP_Text label, bool active, ref Vector3 baseScale, float outlineBase, Color outlineBaseColor)
        {
            if (label == null) return;

            // scale
            var target = active ? baseScale * equipScale : baseScale;
            label.rectTransform.localScale = Vector3.Lerp(label.rectTransform.localScale, target, Time.unscaledDeltaTime * equipLerpSpeed);

            // font weight (bold toggle for immediate clarity)
            if (equipBold)
                label.fontStyle = active ? (label.fontStyle | FontStyles.Bold) : (label.fontStyle & ~FontStyles.Bold);

            // outline “glow”
            var mat = label.fontMaterial; // instance (we made one in Start)
            if (mat != null)
            {
                int idW = TMPro.ShaderUtilities.ID_OutlineWidth;
                int idC = TMPro.ShaderUtilities.ID_OutlineColor;

                float currentW = mat.HasProperty(idW) ? mat.GetFloat(idW) : 0f;
                float targetW  = active ? equipGlowWidth : outlineBase;
                float newW     = Mathf.Lerp(currentW, targetW, Time.unscaledDeltaTime * equipLerpSpeed);
                mat.SetFloat(idW, newW);

                Color currentC = mat.HasProperty(idC) ? mat.GetColor(idC) : Color.black;
                Color targetC  = active ? equipGlowColor : outlineBaseColor;
                var lerped     = Color.Lerp(currentC, targetC, Time.unscaledDeltaTime * equipLerpSpeed);
                mat.SetColor(idC, lerped);
            }
        }

        void HideAll()
        {
            // Never disable UI components. Clear content and fade out via alpha.
            if (primaryReloadFill != null) { primaryReloadFill.fillAmount = 0f; SetAlpha(primaryReloadFill, 0f); }
            if (primaryAmmoText   != null) { primaryAmmoText.text = "";         SetAlpha(primaryAmmoText,   0f); }
            if (primaryWeaponName != null) { primaryWeaponName.text = "";       SetAlpha(primaryWeaponName, 0f); }

            if (secondaryReloadFill != null) { secondaryReloadFill.fillAmount = 0f; SetAlpha(secondaryReloadFill, 0f); }
            if (secondaryAmmoText   != null) { secondaryAmmoText.text = "";         SetAlpha(secondaryAmmoText,   0f); }
            if (secondaryWeaponName != null) { secondaryWeaponName.text = "";       SetAlpha(secondaryWeaponName, 0f); }

            if (meleeWeaponName   != null) { meleeWeaponName.text = "";       SetAlpha(meleeWeaponName,   0f); }
            if (utilityAmmoText   != null) { utilityAmmoText.text = "";       SetAlpha(utilityAmmoText,   0f); }
            if (utilityWeaponName != null) { utilityWeaponName.text = "";     SetAlpha(utilityWeaponName, 0f); }
        }

        // ===== Helpers: defaults, tint logic, pulse animation =====
        void CacheDefaults()
        {
            if (primaryAmmoText != null)   _primaryAmmoBaseColor = primaryAmmoText.color;
            if (secondaryAmmoText != null) _secondaryAmmoBaseColor = secondaryAmmoText.color;

            if (primaryReloadFill != null)
            {
                _primaryReloadBaseScale = primaryReloadFill.rectTransform.localScale;
                _primaryReloadBaseColor = primaryReloadFill.color;
            }
            if (secondaryReloadFill != null)
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
            if (img == null) return;

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
            if (nm == null) return;
            var local = nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
            if (local == null) return;

            _primary   = local.GetComponent<WeaponPrimaryController>();
            _secondary = local.GetComponent<WeaponSecondaryController>();
            _melee     = local.GetComponent<WeaponMeleeController>();
            _utility   = local.GetComponent<WeaponUtilityController>();
            _player    = local.GetComponent<Game.Net.PlayerNetwork>();
        }

        // ===== Helpers: alpha without disabling components =====
        static void SetAlpha(TMP_Text t, float a)
        {
            if (t == null) return;
            var c = t.color; c.a = a; t.color = c;
        }
        static void SetAlpha(Image img, float a)
        {
            if (img == null) return;
            var c = img.color; c.a = a; img.color = c;
        }

        static void EnsureFilled(Image img)
        {
            if (img == null) return;
            if (img.type != Image.Type.Filled) img.type = Image.Type.Filled;
        }

        void MakeMaterialInstance(TMP_Text t, ref float baseOutlineWidth, ref Color baseOutlineColor)
        {
            if (t == null) return;
            // Force a unique material instance so outline animation doesn't modify shared materials
            t.fontMaterial = Instantiate(t.fontMaterial);
            var m = t.fontMaterial;
            int idW = TMPro.ShaderUtilities.ID_OutlineWidth;
            int idC = TMPro.ShaderUtilities.ID_OutlineColor;
            baseOutlineWidth = m.HasProperty(idW) ? m.GetFloat(idW) : 0f;
            baseOutlineColor = m.HasProperty(idC) ? m.GetColor(idC) : Color.black;
        }
    }
}
