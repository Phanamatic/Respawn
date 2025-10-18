using UnityEngine;

namespace Game.Net.Weapons
{
    /// Assign on the player prefab. Drag the three empty points in Inspector.
    public sealed class PlayerWeaponSockets : MonoBehaviour
    {
        public Transform handMount;   // where weapon.grip attaches
        public Transform equipStart;  // high point at equip
        public Transform front;       // normal forward hold/aim
    }
}