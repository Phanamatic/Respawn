using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;            // Resolve ambiguity between System.Diagnostics.Debug and UnityEngine.Debug

namespace Game.Net
{
    public partial class PlayerNetwork
    {
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        private void DbgDumpWeaponsEnv(string where)
        {
            // Resolve Hand Mount safely without relying on private fields.
            Transform hand = null;
            var trs = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < trs.Length; i++)
            {
                if (trs[i].name == "Hand Mount") { hand = trs[i]; break; }
            }

            // Count children & list a few by name (caps to avoid spam)
            int childCount = hand ? hand.childCount : -1;
            var childNames = "";
            if (hand)
            {
                var max = Mathf.Min(childCount, 6);
                for (int i = 0; i < max; i++)
                {
                    var ch = hand.GetChild(i);
                    childNames += (i == 0 ? "" : ", ") + ch.name;
                }
                if (childCount > max) childNames += $", +{childCount - max} more";
            }

            bool hasPrimary  = GetComponentInChildren<Game.Net.Weapons.WeaponPrimaryController>(true)  != null;
            bool hasSecondary= GetComponentInChildren<Game.Net.Weapons.WeaponSecondaryController>(true)!= null;
            bool hasMelee    = GetComponentInChildren<Game.Net.Weapons.WeaponMeleeController>(true)    != null;
            bool hasUtility  = GetComponentInChildren<Game.Net.Weapons.WeaponUtilityController>(true)  != null;

            var scene = SceneManager.GetActiveScene().name;
            byte slotVal = 255;
            try { slotVal = _activeSlot.Value; } catch { /* ignore if not ready */ }

            Debug.Log(
                $"[Weapons][EnvDump] {where} | owner={(IsOwner ? 1 : 0)} " +
                $"slot={slotVal} hasP={hasPrimary} hasS={hasSecondary} hasM={hasMelee} hasU={hasUtility} " +
                $"hand={(hand ? hand.name : "<none>")} handChildren={childCount} [{childNames}] " +
                $"phase={_phase} scene='{scene}'");
        }
    }
}
