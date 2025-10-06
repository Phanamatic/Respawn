using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UI.Scripts; // WeaponData

namespace Game.Net
{
    // Simple row prefab controller used inside the ScrollView content.
    public sealed class LoadoutItemView : MonoBehaviour
    {
        [SerializeField] Image icon;
        [SerializeField] TMP_Text title;
        [SerializeField] TMP_Text stats;   // e.g. "DMG 35  •  FR 8.0  •  AMMO 30"
        [SerializeField] Button equipButton;

        WeaponData _data;
        System.Action<WeaponData> _onEquip;

        public void Bind(WeaponData data, System.Action<WeaponData> onEquip)
        {
            _data = data;
            _onEquip = onEquip;

            if (icon)  icon.sprite = data.weaponIcon;
            if (title) title.text  = data.weaponName;

            if (stats)
            {
                // Minimal stat line. Adjust format to your liking.
                stats.text = $"DMG {Mathf.RoundToInt(data.damage)}  •  FR {data.fireRate:0.##}  •  AMMO {data.ammo}";
            }

            if (equipButton)
            {
                equipButton.onClick.RemoveAllListeners();
                equipButton.onClick.AddListener(() => _onEquip?.Invoke(_data));
            }
        }
    }
}
