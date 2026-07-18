using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The per-tick interest-grid rebuild sharing: a home cell's <see cref="InterestGrid"/> is rebuilt once per server
/// serve pass (epoch), shared by every client homed to it, instead of once per client. Asserted at the
/// <see cref="CellSim"/> level (the epoch guard) and the <see cref="ShardHost"/> level (two clients, one cell, one
/// rebuild) via the <c>InterestRebuildCount</c> seam, plus the cross-epoch correctness that a new epoch always
/// picks up world mutations.
/// </summary>
public class ShardInterestSharingTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static CellSim NewCell() => new(new CellCoord(0, 0), tickSeconds: 0.1f, Registry(), interestCellSize: 100f);

    private static Entity Spawn(CellSim cell, long netId, float x, float y)
    {
        Entity e = cell.World.Spawn();
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    // --- CellSim epoch guard ---

    [Fact]
    public void RebuildInterestShared_RebuildsOncePerEpoch()
    {
        CellSim cell = NewCell();
        Spawn(cell, 1, 10f, 10f);

        cell.RebuildInterestShared(PosAccessor, serveEpoch: 1);
        cell.RebuildInterestShared(PosAccessor, serveEpoch: 1);   // same epoch: reuse
        cell.RebuildInterestShared(PosAccessor, serveEpoch: 1);
        Assert.Equal(1, cell.InterestRebuildCount);

        cell.RebuildInterestShared(PosAccessor, serveEpoch: 2);   // new epoch: rebuild
        Assert.Equal(2, cell.InterestRebuildCount);
    }

    [Fact]
    public void RebuildInterestShared_NewEpoch_PicksUpMovement()
    {
        CellSim cell = NewCell();
        Entity e = Spawn(cell, 1, 10f, 10f);

        cell.RebuildInterestShared(PosAccessor, serveEpoch: 1);
        Assert.Contains(1L, cell.Interest.Query(10f, 10f, 1f));   // indexed at its start position

        // Move it, but reuse the same epoch: the grid is NOT rebuilt, so the query still reads the old position.
        cell.World.Set(e, new Pos { X = 90f, Y = 90f });
        cell.RebuildInterestShared(PosAccessor, serveEpoch: 1);
        Assert.Contains(1L, cell.Interest.Query(10f, 10f, 1f));   // still at the stale spot
        Assert.DoesNotContain(1L, cell.Interest.Query(90f, 90f, 1f));

        // A new epoch (the next tick) rebuilds and reflects the move.
        cell.RebuildInterestShared(PosAccessor, serveEpoch: 2);
        Assert.DoesNotContain(1L, cell.Interest.Query(10f, 10f, 1f));
        Assert.Contains(1L, cell.Interest.Query(90f, 90f, 1f));
    }

    [Fact]
    public void RebuildInterest_Unconditional_AlwaysRebuilds()
    {
        // The default public contract is unchanged: RebuildInterest rebuilds on every call regardless of any epoch.
        CellSim cell = NewCell();
        Spawn(cell, 1, 10f, 10f);

        cell.RebuildInterest(PosAccessor);
        cell.RebuildInterest(PosAccessor);
        Assert.Equal(2, cell.InterestRebuildCount);
    }

    // --- ShardHost serve pass ---

    private static ShardHost ServingHost() =>
        new(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 30f, positionAccessor: PosAccessor);

    private static void SpawnOwned(ShardHost host, int netId, float x, float y)
    {
        Entity e = host.SpawnAt(x, y, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
    }

    [Fact]
    public void HomeInterest_WithSharedEpoch_RebuildsHomeCellOncePerTick_AcrossClients()
    {
        ShardHost host = ServingHost();
        SpawnOwned(host, 1, 30f, 50f);   // both players in cell (0,0)
        SpawnOwned(host, 2, 70f, 50f);
        host.BindClient(0, 1);
        host.BindClient(1, 2);
        Assert.True(host.TryGetHomeCell(0, out CellSim home));

        // Tick's serve pass: two clients homed to the same cell, one shared epoch -> one rebuild.
        host.HomeInterest(0, interestRadius: 30f, serveEpoch: 1);
        host.HomeInterest(1, interestRadius: 30f, serveEpoch: 1);
        Assert.Equal(1, home.InterestRebuildCount);

        // Next tick, fresh epoch -> one more rebuild (still shared by both clients).
        host.HomeInterest(0, interestRadius: 30f, serveEpoch: 2);
        host.HomeInterest(1, interestRadius: 30f, serveEpoch: 2);
        Assert.Equal(2, home.InterestRebuildCount);
    }

    [Fact]
    public void HomeInterest_WithoutEpoch_RebuildsPerCall()
    {
        // The pre-existing default (no serve epoch) rebuilds on every call, preserving the direct-call contract.
        ShardHost host = ServingHost();
        SpawnOwned(host, 1, 30f, 50f);
        SpawnOwned(host, 2, 70f, 50f);
        host.BindClient(0, 1);
        host.BindClient(1, 2);
        Assert.True(host.TryGetHomeCell(0, out CellSim home));

        host.HomeInterest(0, interestRadius: 30f);
        host.HomeInterest(1, interestRadius: 30f);
        Assert.Equal(2, home.InterestRebuildCount);
    }
}
