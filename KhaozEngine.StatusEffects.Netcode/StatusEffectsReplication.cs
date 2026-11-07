using System;
using System.Collections.Immutable;
using System.IO;
using KhaozEngine.Replication;

namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>A bounded codec for transient effect windows, shared by server and client registries.</summary>
public static class StatusEffectsReplication
{
    public const int MaxEntries = 255;

    public static ReplicationChannels Channels => ReplicationChannels.Replicate | ReplicationChannels.Migrate;

    /// <summary>Registers at a caller-selected extension id, identical on the server and clients.</summary>
    public static void Register(ReplicationRegistry registry, ushort typeId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<ActiveStatusEffects>(typeId, Write, Read, channels: Channels);
    }

    internal static void Write(ActiveStatusEffects state, BinaryWriter writer)
    {
        ImmutableArray<StatusEffectEntry> entries = state.Entries;
        if (entries.Length > MaxEntries)
            throw new InvalidDataException("Too many status effect entries.");
        foreach (var entry in entries)
            Validate(entry);

        writer.Write((byte)entries.Length);
        foreach (var entry in entries)
        {
            writer.Write(entry.Effect.Value);
            writer.Write(entry.Source);
            writer.Write((byte)entry.Stacks);
            writer.Write(entry.StartTick);
            writer.Write(entry.EndTick);
        }
    }

    internal static ActiveStatusEffects Read(BinaryReader reader)
    {
        int count = reader.ReadByte();
        if (count > MaxEntries)
            throw new InvalidDataException("Too many status effect entries.");
        var entries = ImmutableArray.CreateBuilder<StatusEffectEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = new StatusEffectEntry(new(reader.ReadInt32()), reader.ReadInt64(), reader.ReadByte(),
                reader.ReadInt64(), reader.ReadInt64());
            Validate(entry);
            entries.Add(entry);
        }
        return new ActiveStatusEffects { Entries = entries.MoveToImmutable() };
    }

    private static void Validate(StatusEffectEntry entry)
    {
        if (!entry.Effect.IsValid)
            throw new InvalidDataException("A status effect id must be nonzero.");
        if (entry.Stacks is < 1 or > byte.MaxValue)
            throw new InvalidDataException("Status effect stacks must be between 1 and 255.");
        if (entry.EndTick < entry.StartTick)
            throw new InvalidDataException("A status effect must end at or after its start tick.");
    }
}
