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
