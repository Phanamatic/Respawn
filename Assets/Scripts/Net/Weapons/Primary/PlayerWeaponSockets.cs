using UnityEngine;

namespace Game.Net.Weapons
{
    /// Assign on the player prefab. Drag the three empty points in Inspector.
    public sealed class PlayerWeaponSockets : MonoBehaviour
    {
        public Transform handMount;   // where weapon.grip attaches; Z+ roughly points forward from the palm, Y+ is up
        public Transform equipStart;  // high point at equip (raise position)
        public Transform front;       // normal forward hold/aim; place slightly in front of the hand, forward = aim, up = "blade up" for melee
// Brief dev comment: for knives, tip alignment uses front.position for forward and front.up to decide which way the blade points.

        void Awake()
        {
            Debug.Log($"[Sockets] player='{gameObject.name}' handMount={(handMount ? handMount.name : "null")} equipStart={(equipStart ? equipStart.name : "null")} front={(front ? front.name : "null")}");
        }
    }
}