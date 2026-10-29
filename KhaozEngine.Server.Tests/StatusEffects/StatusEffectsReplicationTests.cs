using System;
using System.Collections.Immutable;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.StatusEffects;
using KhaozEngine.StatusEffects.Netcode;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public class StatusEffectsReplicationTests
{
    private const ushort TypeId = ReplicationRegistry.FirstExtensionTypeId;

    [Fact]
    public void Codec_RoundTripsEntries()
    {
        var entries = ImmutableArray.Create(
            new StatusEffectEntry(new(7), long.MinValue + 9, 3, 4_000_000_000, 4_000_000_060),
            new StatusEffectEntry(new(-11), long.MaxValue, 255, 0, long.MaxValue));

        byte[] snapshot = Encode(new ActiveStatusEffects { Entries = entries });
        Assert.Equal<StatusEffectEntry>(entries, Decode(snapshot).Entries);

        // Read the payload independently so symmetric codec mistakes cannot change the wire contract.
        using var reader = new BinaryReader(new MemoryStream(snapshot));
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(42L, reader.ReadInt64());
        Assert.Equal(TypeId, reader.ReadUInt16());
        Assert.Equal(59, reader.Read7BitEncodedInt());
        Assert.Equal(2, reader.ReadByte());
        Assert.Equal(7, reader.ReadInt32());
        Assert.Equal(long.MinValue + 9, reader.ReadInt64());
        Assert.Equal(3, reader.ReadByte());
        Assert.Equal(4_000_000_000L, reader.ReadInt64());
        Assert.Equal(4_000_000_060L, reader.ReadInt64());
        Assert.Equal(-11, reader.ReadInt32());
        Assert.Equal(long.MaxValue, reader.ReadInt64());
        Assert.Equal(255, reader.ReadByte());
        Assert.Equal(0L, reader.ReadInt64());
        Assert.Equal(long.MaxValue, reader.ReadInt64());
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Fact]
    public void Codec_RoundTripsEmpty()
    {
        Assert.Empty(default(ActiveStatusEffects).Entries);
        Assert.Empty(Decode(Encode(default)).Entries);
        Assert.Empty(Decode(Encode(ActiveStatusEffects.Empty)).Entries);
    }

    [Fact]
    public void Codec_RoundTripsMaximumEntries()
    {
        var entries = ImmutableArray.CreateBuilder<StatusEffectEntry>(255);
        for (int i = 0; i < 255; i++)
            entries.Add(new(new(i + 1), i, 255, 20, 80));
        ImmutableArray<StatusEffectEntry> expected = entries.MoveToImmutable();

        Assert.Equal<StatusEffectEntry>(expected, Decode(Encode(new ActiveStatusEffects { Entries = expected })).Entries);
    }

    [Fact]
    public void Codec_RefusesOversizedCount()
    {
        var entries = ImmutableArray.CreateBuilder<StatusEffectEntry>(256);
        for (int i = 0; i < 256; i++)
            entries.Add(new(new(i + 1), 0, 1, 20, 80));
        var state = new ActiveStatusEffects { Entries = entries.MoveToImmutable() };

        Assert.Throws<InvalidDataException>(() => Encode(state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public void Codec_WriterRefusesInvalidStacks(int stacks)
    {
        var state = new ActiveStatusEffects
        {
            Entries = ImmutableArray.Create(new StatusEffectEntry(new(7), 0, stacks, 20, 80))
        };

        Assert.Throws<InvalidDataException>(() => Encode(state));
    }

    [Fact]
    public void Codec_RefusesZeroStacks() =>
        Assert.Throws<InvalidDataException>(() => Decode(Payload(effect: 7, stacks: 0, start: 20, end: 80)));

    [Fact]
    public void Codec_RefusesZeroEffect() =>
        Assert.Throws<InvalidDataException>(() => Decode(Payload(effect: 0, stacks: 1, start: 20, end: 80)));

    [Theory]
    [InlineData(0, 20, 80)]
    [InlineData(7, 80, 20)]
    public void Codec_WriterRefusesInvalidEffectWindows(int effect, long start, long end)
    {
        var state = new ActiveStatusEffects
        {
            Entries = ImmutableArray.Create(new StatusEffectEntry(new(effect), 0, 1, start, end))
        };

        Assert.Throws<InvalidDataException>(() => Encode(state));
    }

    [Fact]
    public void Codec_RefusesEndBeforeStart() =>
        Assert.Throws<InvalidDataException>(() => Decode(Payload(effect: 7, stacks: 1, start: 80, end: 20)));

    [Fact]
    public void Codec_AcceptsZeroLengthWindow()
    {
        StatusEffectEntry entry = Assert.Single(Decode(Payload(effect: 7, stacks: 1, start: 20, end: 20)).Entries);

        Assert.Equal(new StatusEffectEntry(new(7), 99, 1, 20, 20), entry);
    }

    [Fact]
    public void Codec_RefusesTruncatedEntries()
    {
        byte[] snapshot = Payload(effect: 7, stacks: 1, start: 20, end: 80);
        // Declare two entries while retaining just one complete entry inside the extension frame.
        snapshot[15] = 2;

        Assert.Throws<EndOfStreamException>(() => Decode(snapshot));
    }

    [Fact]
    public void Register_UsesReplicateAndMigrateWithoutPersist()
    {
        ReplicationRegistry registry = Registry();

        Assert.True(registry.IsRegistered<ActiveStatusEffects>(TypeId,
            ReplicationChannels.Replicate | ReplicationChannels.Migrate));
        Assert.False(registry.IsRegistered<ActiveStatusEffects>(TypeId, ReplicationChannels.Default));
    }

    private static ReplicationRegistry Registry()
    {
        var registry = new ReplicationRegistry();
        StatusEffectsReplication.Register(registry, TypeId);
        return registry;
    }

    private static byte[] Encode(ActiveStatusEffects state)
    {
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(42));
        world.Set(entity, state);
        return SnapshotWriter.Write(world, Registry());
    }

    private static ActiveStatusEffects Decode(byte[] snapshot)
    {
        var world = new World();
        var view = new ClientReplicationView(Registry());
        view.Apply(world, snapshot);
        Assert.True(view.TryGetEntity(42, out Entity entity));
        return world.Get<ActiveStatusEffects>(entity);
    }

    private static byte[] Payload(int effect, byte stacks, long start, long end)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1); // Entity count.
        writer.Write(42L);
        writer.Write(TypeId);
        writer.Write7BitEncodedInt(30); // One count byte and one 29-byte entry.
        writer.Write((byte)1);
        writer.Write(effect);
        writer.Write(99L);
        writer.Write(stacks);
        writer.Write(start);
        writer.Write(end);
        writer.Write((ushort)0);
        writer.Flush();
        return stream.ToArray();
    }
}
