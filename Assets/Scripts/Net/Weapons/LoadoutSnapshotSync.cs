using System;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace Game.Net.Weapons
{
    /// <summary>
    /// Server-authoritative, atomic snapshot of a player's loadout + active slot.
    /// Replaces ad-hoc RPCs for weapon view rebuilds / slot flips.
    /// </summary>
    public sealed class LoadoutSnapshotSync : NetworkBehaviour
    {
        // Keep ids tiny: map your weapon enum -> byte ids (0=None).
        // Negative reserve means INF; match your current semantics.
        [Serializable]
        public struct Snapshot : INetworkSerializable, IEquatable<Snapshot>
        {
            public byte primaryId;
            public byte secondaryId;
            public byte meleeId;
            public byte utilityId;
            public byte activeSlot; // 0=P,1=S,2=M,3=U
            public ushort version;  // bump on any change
            public sbyte primaryMag;    // clamped >= 0
            public short primaryReserve; // -1 = INF
            public sbyte secondaryMag;
            public short secondaryReserve;
            public FixedString32Bytes primaryName;
            public FixedString32Bytes secondaryName;
            public FixedString32Bytes meleeName;
            public FixedString32Bytes utilityName;
            public bool isReloadingPrimary;
            public float reloadProgressPrimary;
            public bool isReloadingSecondary;
            public float reloadProgressSecondary;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref primaryId);
                s.SerializeValue(ref secondaryId);
                s.SerializeValue(ref meleeId);
                s.SerializeValue(ref utilityId);
                s.SerializeValue(ref activeSlot);
                s.SerializeValue(ref version);

                s.SerializeValue(ref primaryMag);
                s.SerializeValue(ref primaryReserve);
                s.SerializeValue(ref secondaryMag);
                s.SerializeValue(ref secondaryReserve);

                s.SerializeValue(ref primaryName);
                s.SerializeValue(ref secondaryName);
                s.SerializeValue(ref meleeName);
                s.SerializeValue(ref utilityName);

                s.SerializeValue(ref isReloadingPrimary);
                s.SerializeValue(ref reloadProgressPrimary);
                s.SerializeValue(ref isReloadingSecondary);
                s.SerializeValue(ref reloadProgressSecondary);
            }

            public bool Equals(Snapshot other)
            {
                return primaryId == other.primaryId &&
                       secondaryId == other.secondaryId &&
                       meleeId == other.meleeId &&
                       utilityId == other.utilityId &&
                       activeSlot == other.activeSlot &&
                       version == other.version &&
                       primaryMag == other.primaryMag &&
                       primaryReserve == other.primaryReserve &&
                       secondaryMag == other.secondaryMag &&
                       secondaryReserve == other.secondaryReserve &&
                       primaryName.Equals(other.primaryName) &&
                       secondaryName.Equals(other.secondaryName) &&
                       meleeName.Equals(other.meleeName) &&
                       utilityName.Equals(other.utilityName) &&
                       isReloadingPrimary == other.isReloadingPrimary &&
                       reloadProgressPrimary.Equals(other.reloadProgressPrimary) &&
                       isReloadingSecondary == other.isReloadingSecondary &&
                       reloadProgressSecondary.Equals(other.reloadProgressSecondary);
            }
        }

        public NetworkVariable<Snapshot> State = new(
            new Snapshot(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>
        /// Server-only: publish a fresh snapshot (incrementing version if you want it to win client-side).
        /// Call this after you validate loadout and after you force active slot.
        /// </summary>
        public void ServerPublish(in Snapshot snap, bool incrementVersion = true)
        {
            if (!IsServer) return;
            var s = snap;
            if (incrementVersion)
            {
                unchecked { s.version = (ushort)(State.Value.version + 1); }
            }
            State.Value = s;
        }

        /// <summary>
        /// Owner requests a slot change; server validates and publishes atomically.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void RequestSlotChangeServerRpc(byte desiredSlot)
        {
            if (!IsServer) return;

            // *** Insert your own permission/match-state checks here ***

            // Flip server-side, re-validate, then publish.
            var s = State.Value;
            s.activeSlot = desiredSlot;
            unchecked { s.version++; }
            State.Value = s;
        }
    }
}