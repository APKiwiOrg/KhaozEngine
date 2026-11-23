using System;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

[Collection("AllocSensitive")]
public sealed class ClientApplyReuseTests
{
    private const ushort ExtensionId = ReplicationRegistry.FirstExtensionTypeId;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WarmedApplyAllocatesNothingWithoutSampledComponents(bool delta, bool full)
    {
        ReplicationRegistry registry = MakeRegistry(sampled: false);
        var source = new World();
        AddEntity(source, 7, 31, 57);
        byte[] frame = Frame(source, registry, delta, full);
        var world = new World();
        var view = new ClientReplicationView(registry);
        if (delta && !full) Apply(view, world, Frame(source, registry, delta: true, full: true), delta: true);
        for (int i = 0; i < 16; i++) Apply(view, world, frame, delta);

        long bytes = AllocationMeasurement.Measure("Warmed apply", () =>
        {
            for (int i = 0; i < 32; i++) Apply(view, world, frame, delta);
        });
        Assert.Equal(0L, bytes);
        Assert.True(view.TryGetEntity(7, out Entity e) && world.TryGet(e, out Marker marker) && marker.Value == 57);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessiveFullFramesDespawnOmittedIds(bool delta)
    {
        ReplicationRegistry registry = MakeRegistry(sampled: false);
        var source = new World();
        Entity gone = AddEntity(source, 7, 31, 57);
        AddEntity(source, 9, 11, 17);
        byte[] before = Frame(source, registry, delta, full: true);
        source.Despawn(gone);
        AddEntity(source, 11, 83, 19);
        byte[] after = Frame(source, registry, delta, full: true);
        var world = new World();
        var view = new ClientReplicationView(registry);

        Apply(view, world, before, delta);
        Assert.True(view.TryGetEntity(7, out Entity departed));
        Apply(view, world, after, delta);

        Assert.False(view.TryGetEntity(7, out _));
        Assert.False(world.IsAlive(departed));
        Assert.True(view.TryGetEntity(11, out Entity arrived) && world.TryGet(arrived, out Marker marker) && marker.Value == 19);
    }

    [Fact]
    public void DeltaKeepsBracketsAcrossEnterLeaveAndComponentRemoval()
    {
        ReplicationRegistry registry = MakeRegistry(sampled: true);
        var source = new World();
        Entity stay = AddEntity(source, 7, 10, 100);
        Entity gone = AddEntity(source, 8, 100, 200);
        var writer = new ServerReplicator(registry);
        writer.Capture(source);
        byte[] first = writer.WriteFor(0);

        source.AdvanceTick();
        source.Set(stay, new Value(20));
        source.Remove<Marker>(stay);
        source.Despawn(gone);
        AddEntity(source, 9, 30, 300);
        writer.Capture(source);
        byte[] second = writer.WriteFor(0);

        var world = new World();
        var view = new ClientReplicationView(registry);
        Apply(view, world, first, delta: true);
        view.RecordInterpolationSample(0);
        Apply(view, world, second, delta: true);
        view.RecordInterpolationSample(1);
        // Presentation uses its own sample bytes even after the caller overwrites both input arrays.
        Array.Clear(first);
        Array.Clear(second);
        view.InterpolateAt(world, 0.5);

        Assert.Equal(2, view.LastAppliedSeq);
        Assert.False(view.TryGetEntity(8, out _));
        Assert.True(view.TryGetEntity(7, out Entity survivor));
        Assert.True(world.TryGet(survivor, out Value blend));
        Assert.Equal(15f, blend.Number);
        Assert.False(world.TryGet(survivor, out Marker _));
        Assert.True(view.TryGetEntity(9, out Entity entered) && world.TryGet(entered, out Marker marker) && marker.Value == 300);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedApplyKeepsFailureTypeAndCanReadAnotherFrame(bool delta)
    {
        ReplicationRegistry registry = MakeRegistry(sampled: false);
        var source = new World();
        AddEntity(source, 7, 31, 57);
        byte[] frame = Frame(source, registry, delta, full: true);
        var world = new World();
        var view = new ClientReplicationView(registry);

        Assert.Throws<EndOfStreamException>(() => Apply(view, world, frame[..^1], delta));
        Assert.Equal(0L, view.FramedWindowLength);
        Apply(view, world, frame, delta);
        Assert.True(view.TryGetEntity(7, out Entity e) && world.TryGet(e, out Marker marker) && marker.Value == 57);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedApplyNeverReadsPastTheSliceAndDetachesAfterFailure(bool delta)
    {
        ReplicationRegistry registry = MakeRegistry(sampled: false);
        var source = new World();
        AddEntity(source, 7, 31, 57);
        byte[] body = Frame(source, registry, delta, full: true);
        byte[] envelope = new byte[body.Length + 2];
        body.CopyTo(envelope, 2);
        var world = new World();
        var view = new ClientReplicationView(registry);

        void ApplySlice(int size)
        {
            world.AdvanceTick();
            if (delta) view.ApplyDeltaBorrowed(world, envelope.AsMemory(2, size));
            else view.ApplyBorrowed(world, envelope.AsMemory(2, size));
        }

        ApplySlice(body.Length);
        Assert.Throws<EndOfStreamException>(() => ApplySlice(body.Length - 1));
        Assert.False(view.HasBorrowedFrameForTests);
        Assert.Equal(0L, view.FramedWindowLength);
        ApplySlice(body.Length);
        Assert.False(view.HasBorrowedFrameForTests);
        Assert.True(view.TryGetEntity(7, out Entity e) && world.TryGet(e, out Marker marker) && marker.Value == 57);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CutUnframedSampleHasTheBaseFailureType(bool delta)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Marker>(1, static (sample, writer) => writer.Write(sample.Value), static reader =>
        {
            int length = reader.ReadInt32();
            reader.BaseStream.Seek(length, SeekOrigin.Current);
            return new Marker(length);
        }, discreteSample: true);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (delta)
        {
            writer.Write(-1);
            writer.Write(1);
            writer.Write(0);
        }
        writer.Write(1);
        writer.Write(3L);
        if (delta)
        {
            writer.Write(true);
            writer.Write(0);
        }
        writer.Write((ushort)1);
        int sampleStart = (int)stream.Position;
        writer.Write(3);
        byte[] cut = stream.ToArray();
        byte[] sample = new byte[sizeof(int) + 3];
        Assert.Throws<ArgumentException>(() => Array.Copy(cut, sampleStart, sample, 0, sample.Length));

        var view = new ClientReplicationView(registry);
        var world = new World();
        Assert.Throws<ArgumentException>(() => Apply(view, world, cut, delta));

        // Bytes outside the logical window could satisfy the seek if the decoder used the array length.
        byte[] backing = new byte[cut.Length + 5];
        cut.CopyTo(backing, 2);
        ReadOnlyMemory<byte> body = backing.AsMemory(2, cut.Length);
        world.AdvanceTick();
        Assert.Throws<ArgumentException>(() =>
        {
            if (delta) view.ApplyDeltaBorrowed(world, body);
            else view.ApplyBorrowed(world, body);
        });
    }

    [Fact]
    public void FailedUnframedCharacterReadCannotAffectTheNextFrame()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Marker>(1, static (value, writer) => writer.Write((byte)value.Value),
            static reader => new Marker(reader.ReadChar()));
        var view = new ClientReplicationView(registry);
        var world = new World();

        Assert.Throws<EndOfStreamException>(() => Apply(view, world, CharacterSnapshot(1, 0xC3, complete: false), delta: false));
        Apply(view, world, CharacterSnapshot(1, (byte)'q'), delta: false);
        Assert.True(view.TryGetEntity(3, out Entity entity));
        Assert.True(world.TryGet(entity, out Marker letter));
        Assert.Equal((int)'q', letter.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void FramedCharacterReadCannotAffectTheNextWindow(bool sameFrame, bool firstReadThrows)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Marker>(ExtensionId, static (value, writer) => writer.Write((byte)value.Value),
            reader => new Marker(firstReadThrows ? reader.ReadChar() : reader.Read()));
        registry.Register<Value>(ExtensionId + 1, static (value, writer) => writer.Write((byte)value.Number),
            static reader => new Value(reader.ReadChar()));
        var view = new ClientReplicationView(registry);
        var world = new World();

        if (sameFrame)
        {
            Apply(view, world, CharacterSnapshot(ExtensionId, 0xC3, includeFollowing: true), delta: false);
        }
        else
        {
            byte[] first = CharacterSnapshot(ExtensionId, 0xC3);
            if (firstReadThrows)
                Assert.Throws<EndOfStreamException>(() => Apply(view, world, first, delta: false));
            else
                Apply(view, world, first, delta: false);
            Apply(view, world, CharacterSnapshot(ExtensionId + 1, (byte)'q'), delta: false);
        }

        Assert.True(view.TryGetEntity(3, out Entity entity));
        Assert.True(world.TryGet(entity, out Value letter));
        Assert.Equal((float)'q', letter.Number);
    }

    private static byte[] CharacterSnapshot(ushort type, byte value, bool complete = true, bool includeFollowing = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(3L);
        writer.Write(type);
        if (ReplicationRegistry.IsExtension(type)) writer.Write7BitEncodedInt(1);
        writer.Write(value);
        if (includeFollowing)
        {
            writer.Write((ushort)(ExtensionId + 1));
            writer.Write7BitEncodedInt(1);
            writer.Write((byte)'q');
        }
        if (complete) writer.Write((ushort)0);
        return stream.ToArray();
    }

    [Fact]
    public void NestedApplyKeepsTheOuterReaderAndOmissionSet()
    {
        var registry = new ReplicationRegistry();
        var source = new World();
        var target = new World();
        var view = new ClientReplicationView(registry);
        byte[]? nested = null;
        registry.Register<Marker>(ExtensionId, static (c, w) =>
        {
            w.Write(c.Value - 1);
            w.Write(1);
        }, reader =>
        {
            int head = reader.ReadInt32();
            if (nested is not null) view.ApplyDelta(target, nested);
            return new Marker(head + reader.ReadInt32());
        });

        Entity entity = source.Spawn();
        source.Set(entity, new NetId(71));
        source.Set(entity, new Marker(17));
        var writer = new ServerReplicator(registry);
        writer.Capture(source);
        view.ApplyDelta(target, writer.WriteFor(0));
        writer.Capture(source);
        nested = writer.WriteFor(0);

        byte[] outer = SnapshotWriter.Write(source, registry);
        target.AdvanceTick();
        view.Apply(target, outer);
        Assert.Equal(2, view.LastAppliedSeq);
        Assert.True(view.TryGetEntity(71, out Entity e) && target.TryGet(e, out Marker marker) && marker.Value == 17);
        Assert.False(view.HasBorrowedFrameForTests);
    }

    private static ReplicationRegistry MakeRegistry(bool sampled)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Value>(1, static (c, w) => w.Write(c.Number), static r => new Value(r.ReadSingle()),
            lerp: sampled ? static (a, b, t) => new Value(a.Number + (b.Number - a.Number) * t) : null);
        registry.Register<Marker>(ExtensionId, static (c, w) => w.Write(c.Value), static r => new Marker(r.ReadInt32()),
            discreteSample: sampled);
        return registry;
    }

    private static Entity AddEntity(World world, long id, float value, int marker)
    {
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(id));
        world.Set(entity, new Value(value));
        world.Set(entity, new Marker(marker));
        return entity;
    }

    private static byte[] Frame(World source, ReplicationRegistry registry, bool delta, bool full)
    {
        if (!delta) return SnapshotWriter.Write(source, registry);
        var writer = new ServerReplicator(registry);
        if (!full)
        {
            writer.Capture(new World());
            _ = writer.WriteFor(0);
        }
        writer.Capture(source);
        return writer.WriteFor(0);
    }

    private static void Apply(ClientReplicationView view, World world, byte[] body, bool delta)
    {
        world.AdvanceTick();
        if (delta) view.ApplyDelta(world, body);
        else view.Apply(world, body);
    }

    private readonly record struct Value(float Number) : IComponent;
    private readonly record struct Marker(int Value) : IComponent;
}
