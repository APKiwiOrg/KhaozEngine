using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellSimPersistenceTests
{
    private struct Blob : IComponent { public int V; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Blob>(
            typeId: 1,
            write: (b, bw) => bw.Write(b.V),
            read: br => new Blob { V = br.ReadInt32() });
        return r;
    }

    private static CellSim Cell(ReplicationRegistry r) => new(new CellCoord(0, 0), 1f / 30f, r, 10f);

    private static Entity Owned(CellSim c, int netId, int v)
    {
        Entity e = c.World.Spawn();
        c.World.Set(e, new NetId(netId));
        c.World.Set(e, new Blob { V = v });
        return e;
    }

    [Fact]
    public void SnapshotOwned_ExcludesPlayers_Ghosts_AndMigrating()
    {
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);                                  // persistable
        Owned(c, 6, 60);                                  // player (excluded by id)
        Entity ghost = Owned(c, 7, 70); c.World.Set(ghost, new Ghost { Source = new CellCoord(1, 0) });
        Entity mig = Owned(c, 8, 80); c.World.Set(mig, new Migrating { Destination = new CellCoord(1, 0) });

        byte[] snap = c.SnapshotOwned(new HashSet<long> { 6 });

        // Restore into a fresh cell and confirm only NetId 5 survived.
        CellSim restored = Cell(r);
        IReadOnlyList<long> ids = restored.RestoreOwned(snap);
        Assert.Equal(new long[] { 5 }, ids);
        Assert.True(restored.TryGetOwned(5, out Entity e));
        Assert.True(restored.World.TryGet(e, out Blob b));
        Assert.Equal(50, b.V);
    }

    /// <summary>A consumer host is free to write its own SnapshotCell, and nothing forces it to exclude the
    /// players bound in that cell (the engine's own host does, the seam has no parameter for it). Restoring such a
    /// blob into the live cell used to re-point ownership at the stale copy, which then carried the MovementState a
    /// teleport reads its epoch basis from (#653). The restore now refuses a NetId the cell already owns.</summary>
    [Fact]
    public void TryRestoreOwned_NetIdTheCellAlreadyOwns_KeepsTheLiveEntityAndCountsTheSkip()
    {
        ReplicationRegistry r = Registry();
        CellSim source = Cell(r);
        Owned(source, 6, 60);
        byte[] stale = source.SnapshotOwned(new HashSet<long>());   // an empty exclusion set: the player is in it

        CellSim live = Cell(r);
        Entity player = Owned(live, 6, 99);
        live.RegisterOwned(6, player);

        CellRestoreResult result = live.TryRestoreOwned(stale);

        Assert.True(result.Ok);
        Assert.Empty(result.NetIds);                      // nothing was restored, so nothing is reported restored
        Assert.Equal(1, result.SkippedOwnedCount);
        Assert.True(live.TryGetOwned(6, out Entity e));
        Assert.Equal(player, e);                          // ownership still resolves to the live entity
        Assert.True(live.World.TryGet(e, out Blob b));
        Assert.Equal(99, b.V);                            // carrying the live value, not the stale one
        Assert.True(live.ScanOwned(6, out Entity scanned));
        Assert.Equal(player, scanned);                    // and no stale duplicate was left loose in the world
    }

    /// <summary>The guard is not a blanket refusal: everything the cell does not already own restores as before,
    /// in the same call that skips the one it does.</summary>
    [Fact]
    public void TryRestoreOwned_MixedBlob_RestoresTheUnownedAndSkipsTheOwned()
    {
        ReplicationRegistry r = Registry();
        CellSim source = Cell(r);
        Owned(source, 6, 60);
        Owned(source, 7, 70);
        byte[] blob = source.SnapshotOwned(new HashSet<long>());

        CellSim live = Cell(r);
        Entity player = Owned(live, 6, 99);
        live.RegisterOwned(6, player);

        CellRestoreResult result = live.TryRestoreOwned(blob);

        Assert.True(result.Ok);
        Assert.Equal(new long[] { 7 }, result.NetIds);
        Assert.Equal(1, result.SkippedOwnedCount);
        Assert.True(live.TryGetOwned(7, out Entity restored));
        Assert.True(live.World.TryGet(restored, out Blob b));
        Assert.Equal(70, b.V);
    }

    [Fact]
    public void MaxOwnedNetId_ReturnsHighestOwned_ZeroWhenEmpty()
    {
        ReplicationRegistry r = Registry();
        CellSim empty = Cell(r);
        Assert.Equal(0, empty.MaxOwnedNetId());

        CellSim c = Cell(r);
        Owned(c, 3, 30);
        Owned(c, 9, 90);
        Entity ghost = Owned(c, 99, 990); c.World.Set(ghost, new Ghost { Source = new CellCoord(1, 0) });
        Assert.Equal(9, c.MaxOwnedNetId());               // ghost 99 not counted
    }
}
