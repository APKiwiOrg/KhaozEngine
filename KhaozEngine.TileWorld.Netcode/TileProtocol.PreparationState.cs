using System.IO;
using KhaozEngine.Replication;

namespace KhaozEngine.TileWorld.Netcode;

public static partial class TileProtocol
{
    /// <summary>Server-only preparation state, carried across region handoffs without replication or persistence.</summary>
    public const ushort TileCombatPreparationStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 9;

    internal static void RegisterPreparationState(ReplicationRegistry registry) =>
        registry.Register<TileCombatPreparationState>(TileCombatPreparationStateTypeId,
            WritePreparationState, ReadPreparationState, channels: ReplicationChannels.Migrate);

    static void WritePreparationState(TileCombatPreparationState state, BinaryWriter writer)
    {
        writer.Write(state.HasActive);
        TileCombatPreparation active = state.Active;
        writer.Write(active.AttackerNetId);
        writer.Write(active.TargetNetId);
        writer.Write(active.AttackId);
        writer.Write(active.Revision);
        writer.Write(active.PresentationKey);
        writer.Write(active.PrepareTick);
        writer.Write(active.ImpactTick);
        writer.Write(active.StrikeTicks);
        writer.Write(active.CadenceTicks);
        writer.Write(state.LastAttackId);
        writer.Write(state.ReadyNotBeforeTick);
        writer.Write(state.AttackerTeleportEpoch);
        writer.Write(state.TargetTeleportEpoch);
        writer.Write(state.DeferredTick);
    }

    static TileCombatPreparationState ReadPreparationState(BinaryReader reader) => new()
    {
        HasActive = reader.ReadBoolean(),
        Active = new TileCombatPreparation(reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt64(), reader.ReadUInt32(),
            reader.ReadUInt32(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte(), reader.ReadByte()),
        LastAttackId = reader.ReadUInt64(),
        ReadyNotBeforeTick = reader.ReadInt64(),
        AttackerTeleportEpoch = reader.ReadUInt32(),
        TargetTeleportEpoch = reader.ReadUInt32(),
        DeferredTick = reader.ReadInt64()
    };
}
