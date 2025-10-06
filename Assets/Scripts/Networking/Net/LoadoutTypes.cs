using System;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    public enum PrimaryType : byte   { None = 0, AR = 1, SMG = 2, Shotgun = 3, LMG = 4, Sniper = 5 }
    public enum SecondaryType : byte { None = 0, Pistol = 1, MachinePistol = 2 }

    // Single-choice utility
    public enum UtilityType : byte { None = 0, Grenade = 1, Smoke = 2, Stun = 3 }

    [Serializable]
    public struct PlayerLoadout
    {
        public PrimaryType Primary;
        public SecondaryType Secondary;
        public UtilityType Utility;   // Melee is fixed: Knife
        public static PlayerLoadout Default => new PlayerLoadout { Primary = PrimaryType.AR, Secondary = SecondaryType.Pistol, Utility = UtilityType.Grenade };
    }

    public struct NetLoadout : INetworkSerializable, IEquatable<NetLoadout>
    {
        public byte primary;   // PrimaryType
        public byte secondary; // SecondaryType
        public byte util;      // UtilityType

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref primary);
            s.SerializeValue(ref secondary);
            s.SerializeValue(ref util);
        }

        public bool Equals(NetLoadout other) => primary == other.primary && secondary == other.secondary && util == other.util;
        public override int GetHashCode() => (primary << 16) ^ (secondary << 8) ^ util;

        public static NetLoadout From(PlayerLoadout lo) =>
            new NetLoadout { primary = (byte)lo.Primary, secondary = (byte)lo.Secondary, util = (byte)lo.Utility };

        public PlayerLoadout ToModel() =>
            new PlayerLoadout { Primary = (PrimaryType)primary, Secondary = (SecondaryType)secondary, Utility = (UtilityType)util };
    }
}
