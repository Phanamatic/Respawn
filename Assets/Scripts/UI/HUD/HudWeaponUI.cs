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
            bool hasAmmo = _primary.magazineAmmo.Value > 0 || _primary.reserveAmmo.Value != 0;
            
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
                    int magazine = Mathf.Max(0, _primary.magazineAmmo.Value);
                    int reserve = _primary.reserveAmmo.Value;
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    primaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                }
            }
            if (primaryReloadFill)
            {
                primaryReloadFill.fillAmount = _primary.isReloading.Value ? _primary.reloadProgress.Value : 0f;
                primaryReloadFill.enabled = _primary.isReloading.Value;
            }
        }

        void UpdateSecondaryUI()
        {
            if (!_secondary) return;

            bool equipped = _secondary.equippedWeaponName.Value.Length > 0;
            bool hasAmmo = _secondary.magazineAmmo.Value > 0 || _secondary.reserveAmmo.Value != 0;
            
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
                    int magazine = Mathf.Max(0, _secondary.magazineAmmo.Value);
                    int reserve = _secondary.reserveAmmo.Value;
                    string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                    secondaryAmmoText.text = $"{magazine.ToString("D3")}/{reserveText}";
                }
            }
            if (secondaryReloadFill)
            {
                secondaryReloadFill.fillAmount = _secondary.isReloading.Value ? _secondary.reloadProgress.Value : 0f;
                secondaryReloadFill.enabled = _secondary.isReloading.Value;
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