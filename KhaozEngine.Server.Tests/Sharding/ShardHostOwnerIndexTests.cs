using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The netId -&gt; (cell, entity) ownership index (gap 6 of the MMO arch review): <see cref="CellSim.TryGetOwned"/>
/// and <see cref="ShardHost.TryGetOwner"/> resolve owners in O(1) off a maintained index instead of a linear
/// <c>World.ForEach</c>. These tests pin the correctness bar: the index equals a from-scratch scan after every
/// tick (including a migration stress), a ghost never resolves as an owner, and the pre-index raw spawn idiom
/// still resolves (fallback behind the index). The hot lookup's zero-allocation bar is next door in
/// <see cref="ShardHostOwnerIndexAllocationTests"/>, which needs a serialized collection this class does not.
/// </summary>
public class ShardHostOwnerIndexTests
{
    private struct Pos : IComponent { public float X; public float Y; }
    private struct Hp : IComponent { public int Value; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Hp>(2,
            (h, bw) => bw.Write(h.Value),
            br => new Hp { Value = br.ReadInt32() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static ShardHost HandoffHost(ReplicationRegistry registry, float overlapMargin = 0f) =>
        new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin, positionAccessor: PosAccessor);

    // Eager owned-spawn via the new choke-point API: entity lands in the owning cell with its NetId registered.
    private static Entity Spawn(ShardHost host, int netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnOwned(x, y, netId, out cell);
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static void Move(ShardHost host, int netId, float x, float y)
    {
        Assert.True(host.TryGetOwner(netId, out CellSim owner, out Entity e));
        owner.World.Set(e, new Pos { X = x, Y = y });
    }

    /// <summary>Independent from-scratch scan of every cell's world: netId -&gt; the cell that authoritatively
    /// owns it (present, alive, not a ghost, not migrating). The oracle the maintained index must agree with.</summary>
    private static Dictionary<long, CellCoord> GroundTruth(ShardHost host)
    {
        var gt = new Dictionary<long, CellCoord>();
        foreach (CellSim c in host.Cells)
        {
            CellSim cell = c;
            cell.World.ForEach<NetId>((Entity e, ref NetId id) =>
            {
                if (!cell.World.Has<Ghost>(e) && !cell.World.Has<Migrating>(e)) gt[id.Value] = cell.Coord;
            });
        }
        return gt;
    }

    // Assert the maintained index (per-cell owned map + host netId->cell map) equals the ground-truth scan exactly.
    private static void AssertIndexMatchesGroundTruth(ShardHost host)
    {
        Dictionary<long, CellCoord> gt = GroundTruth(host);

        // Host index: same key set, same owning cell.
        Assert.Equal(gt.Count, host.OwnerCellEntries.Count);
        foreach ((long netId, CellCoord coord) in gt)
        {
            Assert.True(host.OwnerCellEntries.TryGetValue(netId, out CellCoord got),
                $"host index missing owned netId {netId}");
            Assert.Equal(coord, got);
        }

        // Per-cell owned map: each cell's index holds exactly the netIds ground truth says it owns.
        foreach (CellSim cell in host.Cells)
        {
            var expected = new HashSet<long>();
            foreach ((long netId, CellCoord coord) in gt)
                if (coord == cell.Coord) expected.Add(netId);

            Assert.Equal(expected.Count, cell.OwnedIndexEntries.Count);
            foreach (int netId in expected)
                Assert.True(cell.OwnedIndexEntries.ContainsKey(netId),
                    $"cell {cell.Coord} index missing owned netId {netId}");
        }
    }

    [Fact]
    public void OwnerIndex_MatchesGroundTruthScan_AfterEveryMigrationTick()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);

        // A spread of entities across several cells.
        const int count = 40;
        for (int i = 0; i < count; i++)
            Spawn(host, netId: 1000 + i, x: (i % 8) * 100f + 50f, y: (i / 8) * 100f + 50f, out _);
        AssertIndexMatchesGroundTruth(host);

        // Deterministic sweep that forces many boundary crossings in both axes over many ticks.
        for (int tick = 0; tick < 60; tick++)
        {
            for (int i = 0; i < count; i++)
            {
                int netId = 1000 + i;
                // Each entity drifts on a per-entity phase so crossings are staggered, not synchronized.
                float t = tick + i * 0.37f;
                float x = 50f + 350f * (0.5f + 0.5f * MathF.Sin(t * 0.5f));
                float y = 50f + 250f * (0.5f + 0.5f * MathF.Cos(t * 0.31f + i));
                Move(host, netId, x, y);
            }
            host.Tick(0.1f);
            host.ProcessHandoffs();
            AssertIndexMatchesGroundTruth(host);
            for (int i = 0; i < count; i++)
                Assert.Equal(1, host.OwnerCount(1000 + i)); // exactly-once invariant re-asserted every tick
        }
    }

    [Fact]
    public void Ghost_NeverResolvesAsOwner()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry, overlapMargin: 20f);
        host.CellFor(150f, 50f);                     // B=(1,0) exists to receive a ghost
        Spawn(host, 7, 90f, 50f, out CellSim a);     // owned by A=(0,0), within 20 of the east edge
        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(b.TryGetGhost(7, out _));        // B holds a ghost of 7

        // Owner resolves to A, never to B (the cell holding the ghost).
        Assert.True(host.TryGetOwner(7, out CellSim owner, out _));
        Assert.Equal(a.Coord, owner.Coord);
        Assert.False(b.TryGetOwned(7, out _));       // B does not own the ghost
        Assert.False(b.OwnedIndexEntries.ContainsKey(7)); // the ghost never enters B's owned index
        Assert.Equal(a.Coord, host.OwnerCellEntries[7]);
        AssertIndexMatchesGroundTruth(host);
    }

    [Fact]
    public void Despawn_RemovesFromIndex_AndLeavesGroundTruthAgreement()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        Spawn(host, 1, 50f, 50f, out CellSim a);
        Spawn(host, 2, 50f, 50f, out _);
        Spawn(host, 3, 150f, 50f, out _);

        Assert.True(host.TryGetOwner(2, out CellSim owner2, out Entity e2));
        owner2.UnregisterOwned(2);
        owner2.World.Despawn(e2);

        Assert.False(host.TryGetOwner(2, out _, out _));
        Assert.False(a.OwnedIndexEntries.ContainsKey(2));
        Assert.False(host.OwnerCellEntries.ContainsKey(2));
        AssertIndexMatchesGroundTruth(host);
        Assert.Equal(0, host.OwnerCount(2));
        Assert.Equal(1, host.OwnerCount(1));
        Assert.Equal(1, host.OwnerCount(3));
    }

    [Fact]
    public void RawSpawnIdiom_WithoutRegister_StillResolvesViaFallbackBehindTheIndex()
    {
        // Pre-index consumers spawn owned entities as SpawnAt + World.Set(NetId) without registering. The index
        // must not silently break them: a lookup miss falls through to the scan behind the index, finds the owner,
        // and caches it so subsequent lookups are O(1).
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        Entity e = host.SpawnAt(70f, 40f, out CellSim cell);
        cell.World.Set(e, new NetId(99));
        cell.World.Set(e, new Pos { X = 70f, Y = 40f });

        Assert.False(cell.OwnedIndexEntries.ContainsKey(99)); // not eagerly indexed (no register)
        Assert.True(host.TryGetOwner(99, out CellSim owner, out Entity found));
        Assert.Equal(cell.Coord, owner.Coord);
        Assert.Equal(e, found);
        Assert.True(cell.OwnedIndexEntries.ContainsKey(99));  // the fallback cached it
    }

}

/// <summary>
/// The zero-allocation half of <see cref="ShardHostOwnerIndexTests"/>, in a class of its own because it belongs in
/// the non-parallel <c>AllocSensitive</c> collection (see <see cref="AllocSensitiveCollection"/>) and the four
/// correctness rows next door do not: serializing them would cost the whole migration stress on every run for no
/// benefit. Its assertion reads <c>GC.GetAllocatedBytesForCurrentThread()</c>, which is per thread but not immune
/// to the rest of the assembly, because xUnit reuses worker threads across collections and a gen-0 collection
/// triggered by a parallel class can land inside the measurement window and be attributed here.
/// <para>That is issue #264, nine sightings across CI legs and dev machines measuring 440, 1464, 1944, 2360, 5976,
/// 7384 and 8192 bytes, every one of them passing in isolation immediately afterwards. Two defences, matching
/// what the assembly's other allocation-sensitive classes already carry: this collection keeps the measurement off
/// the parallel pool, and <see cref="AllocAssert.NoPerCallAllocation"/> retries once so a single stray collection
/// cannot fail the run while a real per-lookup allocation still fails both passes.</para>
/// </summary>
[Collection("AllocSensitive")]
public class ShardHostOwnerIndexAllocationTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    [Fact]
    public void TryGetOwner_HotLookups_AllocateNothing()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        ShardHost host = new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin: 0f, positionAccessor: PosAccessor);

        for (int i = 0; i < 16; i++)
        {
            float x = (i % 4) * 100f + 50f;
            float y = (i / 4) * 100f + 50f;
            Entity e = host.SpawnOwned(x, y, netId: 500 + i, out CellSim cell);
            cell.World.Set(e, new Pos { X = x, Y = y });
        }

        void HotLookups()
        {
            for (int loop = 0; loop < 1000; loop++)
                for (int i = 0; i < 16; i++)
                    host.TryGetOwner(500 + i, out _, out _);
        }

        // Warm: populate the index and trigger any first-call JIT before measuring. The measured local runs once
        // here too, so its own first-call cost is outside every measured window.
        for (int warm = 0; warm < 4; warm++)
            for (int i = 0; i < 16; i++)
                Assert.True(host.TryGetOwner(500 + i, out _, out _));
        HotLookups();

        // The O(1) index hit path is allocation-free.
        AllocAssert.NoPerCallAllocation("16000 ShardHost.TryGetOwner index hits", HotLookups);
    }
}
