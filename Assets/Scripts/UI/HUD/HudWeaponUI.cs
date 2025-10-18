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
            FindController();
        }

        void Update()
        {
            if (!_wp) { FindController(); return; }

            if (reloadFill)
            {
                reloadFill.fillAmount = _wp.isReloading.Value ? _wp.reloadProgress.Value : 0f;
                reloadFill.enabled = _wp.isReloading.Value;
            }

            if (ammoText)
            {
                ammoText.text = $"{_wp.magazineAmmo.Value}/{_wp.reserveAmmo.Value}";
            }
        }

        void FindController()
        {
            var local = NetworkManager.Singleton ? NetworkManager.Singleton.SpawnManager?.GetLocalPlayerObject() : null;
            if (!local) return;
            _wp = local.GetComponent<WeaponPrimaryController>();
            if (_wp && weaponName) weaponName.text = "Primary";
        }
    }
}