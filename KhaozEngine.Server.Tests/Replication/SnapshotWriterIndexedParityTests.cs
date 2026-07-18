using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Byte-identity acceptance for the indexed <see cref="SnapshotWriter"/> path (the hot-path optimization that resolves
/// a filtered snapshot's net-id set off a <see cref="WorldSnapshotIndex"/> in O(setCount) instead of a full-world
/// scan, reusing a <see cref="SnapshotScratch"/> stream). The indexed <c>WriteFiltered</c> and the single-entity
/// <c>WriteSingle</c> must produce EXACTLY the bytes the original full-scan <c>WriteFiltered</c> produced, across
/// randomized worlds, interest sets (empty / sparse / dense / full), channels, owner scoping, and retained extension
/// frames. The original static overload is the reference here (it stays untouched), so this pins the new paths against
/// the shipped one directly.
/// </summary>
public class SnapshotWriterIndexedParityTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private struct Vel : IComponent { public float X; public float Y; }        // built-in, optional per entity

    private struct Secret : IComponent { public int V; }                       // owner-only extension (length-prefixed)

    private struct Tag : IComponent { public long V; }                         // plain extension (length-prefixed)

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId;      // 16
    private const ushort TagId = ReplicationRegistry.FirstExtensionTypeId + 1;     // 17

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1, (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Vel>(2, (v, bw) => { bw.Write(v.X); bw.Write(v.Y); },
            br => new Vel { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        r.Register<Tag>(TagId, (t, bw) => bw.Write(t.V), br => new Tag { V = br.ReadInt64() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.Persist | ReplicationChannels.Migrate);
        return r;
    }

    // Builds a random world: entityCount NetId entities (ids 1..entityCount) each with Pos, and each RANDOMLY carrying
    // Vel / Secret / Tag, so the filtered snapshot exercises present-and-absent components and both length-prefixed and
    // unframed ids. Deterministic in the seed.
    private static (World world, long[] netIds, Entity[] entities) BuildWorld(ulong seed, int entityCount)
    {
        var world = new World();
        var rng = new DeterministicRng(seed);
        var netIds = new long[entityCount];
        var entities = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            long netId = i + 1;
            Entity e = world.Spawn();
            world.Set(e, new NetId(netId));
            world.Set(e, new Pos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            if (rng.NextFloat() < 0.6f) world.Set(e, new Vel { X = rng.NextFloat(), Y = rng.NextFloat() });
            if (rng.NextFloat() < 0.4f) world.Set(e, new Secret { V = (int)(rng.NextULong() & 0xFFFF) });
            if (rng.NextFloat() < 0.5f) world.Set(e, new Tag { V = (long)rng.NextULong() });
            netIds[i] = netId;
            entities[i] = e;
        }
        return (world, netIds, entities);
    }

    private static HashSet<long> Subset(ulong seed, long[] all, double fraction)
    {
        var rng = new DeterministicRng(seed);
        var set = new HashSet<long>();
        foreach (long id in all)
            if (rng.NextFloat() < fraction) set.Add(id);
        return set;
    }

    [Theory]
    [InlineData(1UL, 200)]
    [InlineData(2UL, 200)]
    [InlineData(7UL, 512)]
    [InlineData(99UL, 64)]
    public void IndexedWriteFiltered_MatchesFullScan_AcrossSetsChannelsAndOwners(ulong seed, int entityCount)
    {
        ReplicationRegistry registry = NewRegistry();
        (World world, long[] netIds, _) = BuildWorld(seed, entityCount);

        var index = new WorldSnapshotIndex();
        index.Rebuild(world);
        var scratch = new SnapshotScratch();

        // Interest sets across the density spectrum, including empty and full, plus a couple of random subsets.
        var sets = new List<HashSet<long>>
        {
            new HashSet<long>(),                               // empty
            Subset(seed * 31 + 1, netIds, 0.05),               // sparse
            Subset(seed * 31 + 2, netIds, 0.5),                // dense
            new HashSet<long>(netIds),                         // full
        };
        // A set containing ids NOT in the world must be skipped identically by both paths.
        HashSet<long> withGhostIds = Subset(seed * 31 + 3, netIds, 0.3);
        withGhostIds.Add(entityCount + 1000);
        withGhostIds.Add(entityCount + 2000);
        sets.Add(withGhostIds);

        var channels = new[]
        {
            ReplicationChannels.Replicate, ReplicationChannels.Persist,
            ReplicationChannels.Migrate, ReplicationChannels.Default,
        };
        var owners = new long?[] { null, netIds[0], netIds[entityCount / 2], netIds[entityCount - 1], 999_999L };

        foreach (HashSet<long> set in sets)
        foreach (ReplicationChannels channel in channels)
        foreach (long? owner in owners)
        {
            byte[] expected = SnapshotWriter.WriteFiltered(world, registry, set, channel, owner);
            byte[] actual = SnapshotWriter.WriteFiltered(index, scratch, world, registry, set, channel, owner);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(3UL, 200)]
    [InlineData(42UL, 128)]
    public void IndexedWriteFiltered_WithRetainedExtensionFrames_MatchesFullScan(ulong seed, int entityCount)
    {
        ReplicationRegistry registry = NewRegistry();
        (World world, long[] netIds, _) = BuildWorld(seed, entityCount);
        var index = new WorldSnapshotIndex();
        index.Rebuild(world);
        var scratch = new SnapshotScratch();

        // A retained-frame provider that appends one opaque extension frame to every other net id (extension ids only,
        // as the contract requires), so the re-emit path is exercised on both paths identically.
        IReadOnlyList<RetainedComponent>? Retained(long netId) =>
            netId % 2 == 0
                ? new List<RetainedComponent> { new(netId, (ushort)(TagId + 5), new byte[] { 1, 2, 3, (byte)(netId & 0xFF) }) }
                : null;

        HashSet<long> set = Subset(seed * 17 + 5, netIds, 0.4);
        foreach (long? owner in new long?[] { null, netIds[0], netIds[entityCount - 1] })
        {
            byte[] expected = SnapshotWriter.WriteFiltered(
                world, registry, set, ReplicationChannels.Persist, owner, Retained);
            byte[] actual = SnapshotWriter.WriteFiltered(
                index, scratch, world, registry, set, ReplicationChannels.Persist, owner, Retained);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(5UL, 150)]
    [InlineData(88UL, 300)]
    public void WriteSingle_MatchesFullScanOfOneNetId(ulong seed, int entityCount)
    {
        ReplicationRegistry registry = NewRegistry();
        (World world, long[] netIds, Entity[] entities) = BuildWorld(seed, entityCount);
        var scratch = new SnapshotScratch();

        foreach (ReplicationChannels channel in new[]
                 {
                     ReplicationChannels.Replicate, ReplicationChannels.Migrate, ReplicationChannels.Default,
                 })
        foreach (long? owner in new long?[] { null, netIds[0] })
        {
            for (int i = 0; i < entityCount; i += 13) // a spread of entities, not every one, to keep the sweep quick
            {
                long netId = netIds[i];
                byte[] expected = SnapshotWriter.WriteFiltered(
                    world, registry, new HashSet<long> { netId }, channel, owner);
                byte[] actual = SnapshotWriter.WriteSingle(
                    scratch, world, registry, netId, entities[i], channel, owner);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void IndexedPath_ReusesScratch_AndStaysIdenticalAcrossManyCalls()
    {
        // The reused scratch stream must not leak bytes between calls: repeated indexed writes with the same inputs
        // return identical arrays, and interleaving different sets does not corrupt a later call.
        ReplicationRegistry registry = NewRegistry();
        (World world, long[] netIds, _) = BuildWorld(123UL, 256);
        var index = new WorldSnapshotIndex();
        index.Rebuild(world);
        var scratch = new SnapshotScratch();

        HashSet<long> a = Subset(1, netIds, 0.2);
        HashSet<long> b = Subset(2, netIds, 0.7);

        byte[] a1 = SnapshotWriter.WriteFiltered(index, scratch, world, registry, a);
        byte[] b1 = SnapshotWriter.WriteFiltered(index, scratch, world, registry, b);
        byte[] a2 = SnapshotWriter.WriteFiltered(index, scratch, world, registry, a);
        byte[] b2 = SnapshotWriter.WriteFiltered(index, scratch, world, registry, b);

        Assert.Equal(a1, a2);
        Assert.Equal(b1, b2);
        Assert.Equal(SnapshotWriter.WriteFiltered(world, registry, a), a1);
        Assert.Equal(SnapshotWriter.WriteFiltered(world, registry, b), b1);
    }
}
