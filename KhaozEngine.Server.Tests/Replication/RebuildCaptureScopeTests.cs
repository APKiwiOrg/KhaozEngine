using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Format 2 builds share the writer's capture with legacy viewers, scan each distinct world once per capture tick, and
/// retain only compact viewer-only copies: owner-scoped, interest-filtered, and never holding another owner's bytes or
/// Persist/Migrate-only bytes.
/// </summary>
public class RebuildCaptureScopeTests
{
    private struct Value : IComponent { public int Number; }

    private struct Secret : IComponent { public int V; }

    private struct Stored : IComponent { public int V; }

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId;
    private const ushort StoredId = ReplicationRegistry.FirstExtensionTypeId + 2;
    private const int LegacyHeaderBytes = 8;

    public static TheoryData<string> Kinds => RebuildWriterAdapter.Kinds;

    private static readonly HashSet<long> All = new() { 1, 2, 3, 4 };

    private static ReplicationRegistry PlainRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Value>(1, (v, bw) => bw.Write(v.Number), br => new Value { Number = br.ReadInt32() });
        return r;
    }

    private static ReplicationRegistry ScopedRegistry()
    {
        ReplicationRegistry r = PlainRegistry();
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        r.Register<Stored>(StoredId, (s, bw) => bw.Write(s.V), br => new Stored { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist | ReplicationChannels.Migrate);
        return r;
    }

    private static Entity Spawn(World world, long netId, int value)
    {
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new Value { Number = value });
        return e;
    }

    private static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, byte[] needle) => haystack.IndexOf(needle) >= 0;

    private static IReadOnlyList<long> NetIds(ReplicationProjection projection)
    {
        var ids = new List<long>(projection.Entities.Keys);
        ids.Sort();
        return ids;
    }

    [Fact]
    public void CaptureIsSharedAcrossLegacyAndV2Viewers()
    {
        var world = new World();
        for (long i = 1; i <= 4; i++) Spawn(world, i, (int)i);
        var writer = new AoiDeltaReplicator(PlainRegistry());
        writer.StartRebuild(1, 1, new DeltaRebuildOptions());
        writer.StartRebuild(2, 2, new DeltaRebuildOptions());

        writer.BeginTick();
        byte[] legacy = writer.WriteFor(0, world, All);
        ReplicationDeltaPacket full = writer.BuildRebuildFor(1, world, All);
        ReplicationDeltaPacket partial = writer.BuildRebuildFor(2, world, new HashSet<long> { 1, 2 });
        Assert.Equal(1, writer.WorldScanCount);
        Assert.Equal(legacy.AsSpan(LegacyHeaderBytes).ToArray(), full.Bytes.Span[RebuildWire.HeaderBytes..].ToArray());
        Assert.True(writer.TryGetRetainedProjectionForTest(2, partial.Id, out ReplicationProjection scoped));
        Assert.Equal(new long[] { 1, 2 }, NetIds(scoped));

        // Format 2 viewers served first share the tick capture just the same.
        writer.BeginTick();
        writer.BuildRebuildFor(1, world, All);
        writer.BuildRebuildFor(2, world, new HashSet<long> { 1, 2 });
        writer.WriteFor(0, world, All);
        Assert.Equal(2, writer.WorldScanCount);
    }

    [Fact]
    public void WholeWorldBuildUsesTheCurrentCapture()
    {
        var world = new World();
        Entity one = Spawn(world, 1, 1);
        var writer = new ServerReplicator(PlainRegistry());
        writer.StartRebuild(0, 1, new DeltaRebuildOptions());
        writer.Capture(world);
        world.Set(one, new Value { Number = 99 });   // after the capture, so neither viewer may see it

        byte[] legacy = writer.WriteFor(5);
        ReplicationDeltaPacket packet = writer.BuildRebuildFor(0);
        Assert.Equal(legacy.AsSpan(LegacyHeaderBytes).ToArray(), packet.Bytes.Span[RebuildWire.HeaderBytes..].ToArray());
        Assert.True(writer.TryGetRetainedProjectionForTest(0, packet.Id, out ReplicationProjection retained));
        ProjectionDump.AssertEqual(new[] { new ProjectionEntry(1, 1, Int(1)) }, ProjectionDump.Of(retained));
    }

    [Fact]
    public void TwoWorldsEachScanOnce()
    {
        var worldA = new World();
        Spawn(worldA, 1, 10);
        Spawn(worldA, 2, 20);
        var worldB = new World();
        Spawn(worldB, 3, 30);
        Spawn(worldB, 4, 40);
        var writer = new AoiDeltaReplicator(PlainRegistry());
        writer.StartRebuild(1, 1, new DeltaRebuildOptions());
        writer.StartRebuild(3, 3, new DeltaRebuildOptions());
        writer.StartRebuild(4, 4, new DeltaRebuildOptions());

        for (int tick = 1; tick <= 2; tick++)
        {
            writer.BeginTick();
            writer.WriteFor(0, worldA, All);
            ReplicationDeltaPacket a = writer.BuildRebuildFor(1, worldA, All);
            writer.WriteFor(2, worldB, All);
            ReplicationDeltaPacket b = writer.BuildRebuildFor(3, worldB, All);
            ReplicationDeltaPacket b2 = writer.BuildRebuildFor(4, worldB, All);
            Assert.Equal(2 * tick, writer.WorldScanCount);

            Assert.True(writer.TryGetRetainedProjectionForTest(1, a.Id, out ReplicationProjection fromA));
            Assert.True(writer.TryGetRetainedProjectionForTest(3, b.Id, out ReplicationProjection fromB));
            Assert.True(writer.TryGetRetainedProjectionForTest(4, b2.Id, out ReplicationProjection fromB2));
            Assert.Equal(new long[] { 1, 2 }, NetIds(fromA));
            Assert.Equal(new long[] { 3, 4 }, NetIds(fromB));
            ProjectionDump.AssertEqual(ProjectionDump.Of(fromB), ProjectionDump.Of(fromB2));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void PersistedViewerStorageIsOwnerCompact(string kind)
    {
        RebuildWriterAdapter w = RebuildWriterAdapter.Create(kind, ScopedRegistry());
        Entity one = Spawn(w.World, 1, 1);
        w.World.Set(one, new Secret { V = 0x5EC1E701 });
        w.World.Set(one, new Stored { V = 0x570DED01 });
        Entity two = Spawn(w.World, 2, 2);
        w.World.Set(two, new Secret { V = 0x5EC1E702 });
        w.World.Set(two, new Stored { V = 0x570DED02 });
        var interest = new HashSet<long> { 1, 2 };
        w.Start(0, 1);
        w.Start(1, 2);

        w.Capture();
        ReplicationDeltaPacket toOne = w.Build(0, interest, owner: 1);
        ReplicationDeltaPacket toTwo = w.Build(1, interest, owner: 2);
        AssertOwnerCompact(w.Retained(0, toOne.Id), toOne, ownSecret: 0x5EC1E701, otherSecret: 0x5EC1E702, owner: 1);
        AssertOwnerCompact(w.Retained(1, toTwo.Id), toTwo, ownSecret: 0x5EC1E702, otherSecret: 0x5EC1E701, owner: 2);

        // A delta from the acknowledged baseline is retained the same way.
        w.Sent(0, toOne.Id);
        w.Ack(0, toOne.Id);
        w.World.Set(two, new Value { Number = 3 });
        w.Capture();
        ReplicationDeltaPacket delta = w.Build(0, interest, owner: 1);
        Assert.False(delta.IsKeyframe);
        ReplicationProjection retained = w.Retained(0, delta.Id);
        ProjectionDump.AssertEqual(new[]
        {
            new ProjectionEntry(1, 1, Int(1)),
            new ProjectionEntry(1, SecretId, Int(0x5EC1E701)),
            new ProjectionEntry(2, 1, Int(3)),
        }, ProjectionDump.Of(retained));
        Assert.Equal(12, ProjectionDump.DistinctBackingBytes(new[] { retained }));
    }

    private static void AssertOwnerCompact(ReplicationProjection retained, ReplicationDeltaPacket packet, int ownSecret,
        int otherSecret, long owner)
    {
        var expected = new List<ProjectionEntry>
        {
            new(1, 1, Int(1)),
            new(2, 1, Int(2)),
            new(owner, SecretId, Int(ownSecret)),
        };
        expected.Sort((a, b) => a.NetId != b.NetId ? a.NetId.CompareTo(b.NetId) : a.TypeId.CompareTo(b.TypeId));
        ProjectionDump.AssertEqual(expected, ProjectionDump.Of(retained));

        // One exact-size compact buffer holding only visible payloads: 4 + 4 + 4 bytes.
        var backings = new List<byte[]>(retained.BackingArraysForTest());
        Assert.Single(backings);
        Assert.Equal(12, backings[0].Length);
        foreach (int hidden in new[] { otherSecret, 0x570DED01, 0x570DED02 })
        {
            Assert.False(Contains(backings[0], Int(hidden)));
            Assert.False(Contains(packet.Bytes.Span, Int(hidden)));
        }
        Assert.True(Contains(packet.Bytes.Span, Int(ownSecret)));
    }
}
