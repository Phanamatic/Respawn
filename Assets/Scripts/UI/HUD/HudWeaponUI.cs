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
        [SerializeField] Image reloadFill;     // type = Filled
        [SerializeField] TMP_Text ammoText;
        [SerializeField] TMP_Text weaponName;

        WeaponPrimaryController _wp;

        void Start()
        {
            if (reloadFill) reloadFill.enabled = false;
            if (ammoText) { ammoText.enabled = false; ammoText.text = ""; }
            if (weaponName) { weaponName.enabled = false; weaponName.text = ""; }
            FindController();
        }

        void Update()
        {
            if (!_wp) { FindController(); return; }

            // Show only when a weapon is actually equipped
            bool equipped = _wp != null;
            if (weaponName) weaponName.enabled = equipped;
            if (ammoText)   ammoText.enabled   = equipped;

            if (reloadFill)
            {
                reloadFill.fillAmount = _wp.isReloading.Value ? _wp.reloadProgress.Value : 0f;
                reloadFill.enabled = _wp.isReloading.Value;
            }

            if (ammoText)
            {
                int magazine = Mathf.Max(0, _wp.magazineAmmo.Value);
                int reserve = _wp.reserveAmmo.Value;
                string reserveText = reserve < 0 ? "INF" : Mathf.Max(0, reserve).ToString("D3");
                ammoText.text = $"{magazine.ToString("D3")}/{reserveText}";
            }
        }
// Hides equip texts unless equipped; reload image only when reloading.

        void FindController()
        {
            var local = NetworkManager.Singleton ? NetworkManager.Singleton.SpawnManager?.GetLocalPlayerObject() : null;
            if (!local) return;
            _wp = local.GetComponent<WeaponPrimaryController>();
            if (_wp && weaponName) weaponName.text = "Primary";
        }
    }
}