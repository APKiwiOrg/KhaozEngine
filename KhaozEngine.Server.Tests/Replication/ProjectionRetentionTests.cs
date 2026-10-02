using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Retained projections are compact viewer-only copies charged by complete backing allocation, with pins counted in
/// the same count and byte limits and pruning by insertion ordinal. Zero-byte and opaque frames count toward the
/// metadata and byte limits.
/// </summary>
public class ProjectionRetentionTests
{
    private struct Value : IComponent { public int Number; }

    private struct Tag : IComponent { }

    private struct Secret : IComponent { public int V; }

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId;
    private const ushort OpaqueId = ReplicationRegistry.FirstExtensionTypeId + 24;   // registered by nobody

    private static readonly IReadOnlySet<ReplicationPacketId> NoPins = new HashSet<ReplicationPacketId>();

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Value>(1, (v, bw) => bw.Write(v.Number), br => new Value { Number = br.ReadInt32() });
        r.Register<Tag>(2, (_, _) => { }, _ => default);
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        return r;
    }

    private static ReplicationPacketId Id(uint sequence) => new(1, sequence);

    private static HashSet<ReplicationPacketId> Pins(params uint[] sequences)
    {
        var pins = new HashSet<ReplicationPacketId>();
        foreach (uint s in sequences) pins.Add(Id(s));
        return pins;
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static ProjectedComponent Frame(ushort typeId, byte[] backing) => new(typeId, backing, 0, backing.Length);

    private static ReplicationProjection Projection(DeltaRebuildOptions options,
        params (long NetId, ProjectedComponent[] Frames)[] entities)
    {
        var list = new List<KeyValuePair<long, ProjectedEntity>>();
        foreach ((long netId, ProjectedComponent[] frames) in entities)
            list.Add(new(netId, new ProjectedEntity(frames)));
        return ReplicationProjection.Create(list, options);
    }

    // One entity holding one independent backing of the given size.
    private static ReplicationProjection Sized(int bytes, long netId = 1) =>
        Projection(new DeltaRebuildOptions(), (netId, new[] { Frame(OpaqueId, new byte[bytes]) }));

    private static void AssertRetainedBytesExact(ProjectionRetention retention, params ReplicationProjection[] retained)
    {
        Assert.Equal(ProjectionDump.DistinctLength(retention.BackingArraysForTest()), retention.PayloadBytes);
        Assert.Equal(ProjectionDump.DistinctBackingBytes(retained), retention.PayloadBytes);
    }

    [Fact]
    public void ViewerCompactionDoesNotRetainHiddenBacking()
    {
        ReplicationRegistry registry = NewRegistry();
        var world = new World();
        Entity own = world.Spawn();
        world.Set(own, new NetId(1));
        world.Set(own, new Value { Number = 0x11111111 });
        world.Set(own, new Secret { V = 0x5EC12E7A });
        Entity other = world.Spawn();
        world.Set(other, new NetId(2));
        world.Set(other, new Value { Number = 0x22222222 });
        world.Set(other, new Tag());
        world.Set(other, new Secret { V = 0x7A7A7A7A });

        Dictionary<long, CapturedComponents> capture = new CaptureScratch().CaptureReplicate(world, registry);
        var viewer = new Dictionary<long, CapturedComponents>
        {
            [1] = CaptureProjection.OwnerScope(capture[1], registry, 1, ownerNetId: 1),
            [2] = CaptureProjection.OwnerScope(capture[2], registry, 2, ownerNetId: 1),
        };
        Assert.False(viewer[2].Contains(SecretId));

        ReplicationProjection projection = ReplicationProjection.Compact(viewer, new DeltaRebuildOptions());
        var retention = new ProjectionRetention(new DeltaRebuildOptions());
        Assert.True(retention.TryRetain(Id(1), projection, Pins(1), out DeltaRebuildFailure failure));
        Assert.Equal(DeltaRebuildFailure.None, failure);

        ProjectionDump.AssertEqual(new ProjectionEntry[]
        {
            new(1, 1, Int(0x11111111)),
            new(1, SecretId, Int(0x5EC12E7A)),
            new(2, 1, Int(0x22222222)),
            new(2, 2, Array.Empty<byte>()),
        }, ProjectionDump.Of(projection));

        // One independent compact buffer holding exactly the visible payloads, charged at its full length.
        byte[] backing = Assert.Single(retention.BackingArraysForTest());
        Assert.Equal(12, backing.Length);
        Assert.Equal(12, retention.PayloadBytes);
        Assert.Equal(4, retention.Components);
        Assert.Equal(2, retention.Entities);

        // No retained array overlaps the shared capture buffer, hidden owner bytes included.
        foreach ((long netId, CapturedComponents comps) in capture)
            foreach (ushort typeId in comps.TypeIds)
            {
                Assert.True(comps.TryGetSpan(typeId, out ReadOnlySpan<byte> raw));
                if (raw.Length > 0) Assert.False(raw.Overlaps(backing), $"retained backing aliases ({netId},{typeId})");
            }
        Assert.Equal(-1, backing.AsSpan().IndexOf(Int(0x7A7A7A7A)));
    }

    [Fact]
    public void OpaqueAndZeroByteFramesCountTowardLimits()
    {
        ProjectedComponent[] TagFrames(int n)
        {
            var frames = new ProjectedComponent[n];
            for (int i = 0; i < n; i++) frames[i] = Frame((ushort)(OpaqueId + i), Array.Empty<byte>());
            return frames;
        }

        // Zero-byte frames are component frames for the metadata bound.
        var three = new DeltaRebuildOptions { MaxComponents = 3 };
        Assert.Equal(3, Projection(three, (1, TagFrames(3))).ComponentCount);
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded,
            Assert.Throws<DeltaRebuildException>(() => Projection(three, (1, TagFrames(4)))).Failure);

        // Empty entities count toward the entity bound.
        var two = new DeltaRebuildOptions { MaxEntities = 2 };
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, Assert.Throws<DeltaRebuildException>(() =>
            Projection(two, (1, TagFrames(0)), (2, TagFrames(0)), (3, TagFrames(0)))).Failure);

        // Opaque extension bytes count toward the byte bound.
        var sixteen = new DeltaRebuildOptions { MaxRetainedPayloadBytes = 16 };
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, Assert.Throws<DeltaRebuildException>(() =>
            Projection(sixteen, (1, new[] { Frame(OpaqueId, new byte[10]), Frame(OpaqueId + 1, new byte[7]) }))).Failure);

        // Retention enforces its own limits on a projection built under looser ones.
        var loose = new DeltaRebuildOptions();
        ReplicationProjection fourTags = Projection(loose, (1, TagFrames(4)));
        ReplicationProjection opaque = Projection(loose,
            (1, new[] { Frame(OpaqueId, new byte[10]), Frame(OpaqueId + 1, new byte[10]) }));
        var retention = new ProjectionRetention(new DeltaRebuildOptions { MaxComponents = 3, MaxRetainedPayloadBytes = 16 });
        Assert.False(retention.TryRetain(Id(1), fourTags, Pins(1), out DeltaRebuildFailure failure));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, failure);
        Assert.False(retention.TryRetain(Id(2), opaque, Pins(2), out failure));
        Assert.Equal(DeltaRebuildFailure.CapacityExceeded, failure);
        Assert.Equal(0, retention.Count);
        Assert.Empty(retention.Pins);

        ReplicationProjection mixed = Projection(loose,
            (1, new[] { Frame(OpaqueId, new byte[6]), Frame(2, Array.Empty<byte>()), Frame(3, Array.Empty<byte>()) }));
        Assert.True(retention.TryRetain(Id(3), mixed, Pins(3), out failure));
        Assert.Equal(3, retention.Components);
        Assert.Equal(1, retention.Entities);
        Assert.Equal(6, retention.PayloadBytes);
        AssertRetainedBytesExact(retention, mixed);
    }

    [Fact]
    public void PinsCountWithinBudget()
    {
        var retention = new ProjectionRetention(new DeltaRebuildOptions
        {
            MaxRetainedProjections = 4, MaxRetainedPayloadBytes = 100,
        });
        ReplicationProjection a = Sized(30), b = Sized(30), c = Sized(30), d = Sized(30);
        Assert.True(retention.TryRetain(Id(1), a, Pins(1), out _));
        Assert.True(retention.TryRetain(Id(2), b, Pins(1, 2), out _));
        Assert.True(retention.TryRetain(Id(3), c, Pins(1, 2, 3), out _));
        Assert.Equal(90, retention.PayloadBytes);

        // Three pinned projections plus the candidate exceed the byte budget: nothing changes.
        Assert.False(retention.TryRetain(Id(4), d, Pins(1, 2, 3), out DeltaRebuildFailure failure));
        Assert.Equal(DeltaRebuildFailure.RetentionPressure, failure);
        Assert.Equal(3, retention.Count);
        Assert.Equal(90, retention.PayloadBytes);
        Assert.True(retention.Pins.SetEquals(Pins(1, 2, 3)));
        Assert.False(retention.TryGet(Id(4), out _));

        // Unpinning the oldest admits the candidate, which is charged with the remaining pins.
        Assert.True(retention.TryRetain(Id(4), d, Pins(2, 3, 4), out failure));
        Assert.Equal(DeltaRebuildFailure.None, failure);
        Assert.False(retention.TryGet(Id(1), out _));
        Assert.True(retention.TryGet(Id(4), out ReplicationProjection kept));
        Assert.Same(d, kept);
        Assert.Equal(3, retention.Count);
        AssertRetainedBytesExact(retention, b, c, d);

        // More than three prospective pins, or a pin on an id that is not retained, is a caller error.
        Assert.Throws<ArgumentException>(() => retention.TryRetain(Id(5), Sized(1), Pins(2, 3, 4, 5), out _));
        Assert.Throws<ArgumentException>(() => retention.TryRetain(Id(5), Sized(1), Pins(1, 5), out _));
        Assert.Throws<ArgumentException>(() => retention.TryRetain(Id(4), Sized(1), Pins(4), out _));
        Assert.Equal(3, retention.Count);
        Assert.True(retention.Pins.SetEquals(Pins(2, 3, 4)));
        AssertRetainedBytesExact(retention, b, c, d);
    }

    [Fact]
    public void SharedBackingIsChargedOnceAtItsCompleteSize()
    {
        var options = new DeltaRebuildOptions { MaxRetainedProjections = 4 };
        var shared = new byte[64];
        ReplicationProjection first = Projection(options, (1, new[] { new ProjectedComponent(1, shared, 0, 4) }));
        ReplicationProjection second = Projection(options,
            (1, new[] { new ProjectedComponent(1, shared, 8, 4) }),
            (2, new[] { Frame(OpaqueId, new byte[10]) }));
        Assert.Equal(64, first.BackingBytes);
        Assert.Equal(74, second.BackingBytes);

        var retention = new ProjectionRetention(options);
        Assert.True(retention.TryRetain(Id(1), first, Pins(1), out _));
        Assert.Equal(64, retention.PayloadBytes);   // the complete allocation, not the 4-byte slice
        Assert.True(retention.TryRetain(Id(2), second, Pins(2), out _));
        Assert.Equal(74, retention.PayloadBytes);   // the shared array adds nothing the second time
        AssertRetainedBytesExact(retention, first, second);

        // Evicting the first keeps the shared array charged while the second still reaches it.
        Assert.True(retention.TryRetain(Id(3), Sized(1), Pins(2), out _));
        Assert.True(retention.TryRetain(Id(4), Sized(1), Pins(2), out _));
        Assert.True(retention.TryRetain(Id(5), Sized(1), Pins(2), out _));
        Assert.False(retention.TryGet(Id(1), out _));
        Assert.Contains(shared, retention.BackingArraysForTest());
        Assert.Equal(77, retention.PayloadBytes);

        // Once nothing retained reaches it, it is no longer charged.
        Assert.True(retention.TryRetain(Id(6), Sized(1), Pins(6), out _));
        Assert.False(retention.TryGet(Id(2), out _));
        Assert.DoesNotContain(shared, retention.BackingArraysForTest());
        Assert.Equal(4, retention.PayloadBytes);

        retention.Clear();
        Assert.Equal(0, retention.Count);
        Assert.Equal(0, retention.PayloadBytes);
        Assert.Empty(retention.BackingArraysForTest());
        Assert.Empty(retention.Pins);
    }

    [Fact]
    public void OldestUnpinnedInsertionIsPruned()
    {
        var retention = new ProjectionRetention(new DeltaRebuildOptions { MaxRetainedProjections = 4 });
        uint[] order = { uint.MaxValue - 1, uint.MaxValue, 0, 1 };
        foreach (uint s in order) Assert.True(retention.TryRetain(Id(s), Sized(1), NoPins, out _));
        Assert.Equal(4, retention.Count);

        // Insertion order, not numeric order: the wrap makes 0 numerically smallest, but it is not oldest.
        Assert.True(retention.TryRetain(Id(2), Sized(1), NoPins, out _));
        Assert.False(retention.TryGet(Id(uint.MaxValue - 1), out _));
        Assert.True(retention.TryGet(Id(0), out _));

        // A pinned oldest survives and the oldest unpinned goes instead.
        Assert.True(retention.TryRetain(Id(3), Sized(1), Pins(uint.MaxValue), out _));
        Assert.True(retention.TryGet(Id(uint.MaxValue), out _));
        Assert.False(retention.TryGet(Id(0), out _));
        Assert.True(retention.TryGet(Id(1), out _));
        Assert.Equal(4, retention.Count);

        // Byte pressure prunes oldest unpinned first until the budget fits.
        var bytes = new ProjectionRetention(new DeltaRebuildOptions { MaxRetainedPayloadBytes = 25 });
        Assert.True(bytes.TryRetain(Id(10), Sized(10), NoPins, out _));
        Assert.True(bytes.TryRetain(Id(11), Sized(10), Pins(10), out _));
        Assert.True(bytes.TryRetain(Id(12), Sized(5), Pins(10), out _));
        Assert.True(bytes.TryRetain(Id(13), Sized(14), Pins(10), out _));
        Assert.True(bytes.TryGet(Id(10), out _));
        Assert.False(bytes.TryGet(Id(11), out _));
        Assert.False(bytes.TryGet(Id(12), out _));
        Assert.True(bytes.TryGet(Id(13), out _));
        Assert.Equal(24, bytes.PayloadBytes);
    }
}
