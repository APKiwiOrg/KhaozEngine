using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostGhostingTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry PosRegistry()
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

    // cellSize 100, overlap margin 10, position from the Pos component.
    private static ShardHost GhostHost(ReplicationRegistry registry, float margin = 10f) =>
        new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin: margin, positionAccessor: PosAccessor);

    private static Entity SpawnOwned(ShardHost host, int netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnAt(x, y, out cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    [Fact]
    public void EntityWithinMargin_AppearsAsGhostInNeighbor()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);                                  // ensure neighbor B=(1,0) exists
        SpawnOwned(host, 7, 95f, 50f, out _);                     // in A=(0,0), within 10 of east edge x=100

        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(b.TryGetGhost(7, out Entity ghost));
        Assert.Equal(95f, b.World.Get<Pos>(ghost).X);
        Assert.Equal(50f, b.World.Get<Pos>(ghost).Y);
        Assert.True(b.World.Has<Ghost>(ghost));
        Assert.Equal(new CellCoord(0, 0), b.World.Get<Ghost>(ghost).Source);
    }

    [Fact]
    public void EntityBeyondMargin_DoesNotGhost()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);                                  // B exists
        SpawnOwned(host, 7, 50f, 50f, out _);                     // dead center of A, far from every edge

        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.Equal(0, b.GhostCount);
    }

    [Fact]
    public void Owner_KeepsAuthority_GhostIsReadOnlyMirror()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);
        Entity owned = SpawnOwned(host, 7, 95f, 50f, out CellSim a);

        host.SyncGhosts();

        Assert.False(a.World.Has<Ghost>(owned));                  // authoritative in its owner cell
        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(b.TryGetGhost(7, out Entity ghost));
        Assert.True(b.World.Has<Ghost>(ghost));                   // read-only mirror in the neighbor
    }

    [Fact]
    public void MovingOwner_UpdatesGhost_NextSync()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);
        Entity owned = SpawnOwned(host, 7, 95f, 50f, out CellSim a);
        host.SyncGhosts();

        a.World.Set(owned, new Pos { X = 98f, Y = 53f });         // owner moves (still in border)
        host.SyncGhosts();

        host.TryGetCell(new CellCoord(1, 0), out CellSim b);
        Assert.True(b.TryGetGhost(7, out Entity ghost));
        Assert.Equal(98f, b.World.Get<Pos>(ghost).X);
        Assert.Equal(53f, b.World.Get<Pos>(ghost).Y);
    }

    [Fact]
    public void GhostDespawns_WhenOwnerLeavesBorder()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);
        Entity owned = SpawnOwned(host, 7, 95f, 50f, out CellSim a);
        host.SyncGhosts();
        host.TryGetCell(new CellCoord(1, 0), out CellSim b);
        Assert.Equal(1, b.GhostCount);

        a.World.Set(owned, new Pos { X = 40f, Y = 50f });         // moves to A's interior, out of the border
        host.SyncGhosts();

        Assert.Equal(0, b.GhostCount);
        Assert.False(b.TryGetGhost(7, out _));
    }

    [Fact]
    public void CornerEntity_GhostsToThreeNeighbors()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        host.CellFor(150f, 50f);                                  // E=(1,0)
        host.CellFor(50f, 150f);                                  // N=(0,1)
        host.CellFor(150f, 150f);                                 // NE=(1,1)
        SpawnOwned(host, 7, 95f, 95f, out _);                     // near A's NE corner

        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim e) && e.TryGetGhost(7, out _));
        Assert.True(host.TryGetCell(new CellCoord(0, 1), out CellSim n) && n.TryGetGhost(7, out _));
        Assert.True(host.TryGetCell(new CellCoord(1, 1), out CellSim ne) && ne.TryGetGhost(7, out _));
    }

    [Fact]
    public void GhostingTargetsOnlyExistingNeighborCells()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = GhostHost(registry);
        SpawnOwned(host, 7, 95f, 50f, out _);                     // B=(1,0) is NOT created

        host.SyncGhosts();                                        // must not throw, must not create B

        Assert.False(host.TryGetCell(new CellCoord(1, 0), out _));
        Assert.Equal(1, host.CellCount);                          // only A exists
    }

    [Fact]
    public void SyncGhosts_WithMarginButNoPositionAccessor_Throws()
    {
        var host = new ShardHost(100f, 0.1f, new ReplicationRegistry(), interestCellSize: 100f,
            overlapMargin: 10f);                                  // no accessor supplied
        Assert.Throws<InvalidOperationException>(() => host.SyncGhosts());
    }

    [Fact]
    public void OverlapMargin_IsExposed_AndZeroDisablesGhosting()
    {
        ReplicationRegistry registry = PosRegistry();
        var host = new ShardHost(100f, 0.1f, registry, interestCellSize: 100f, overlapMargin: 0f, PosAccessor);
        host.CellFor(150f, 50f);
        SpawnOwned(host, 7, 99f, 50f, out _);                     // right on the edge, but margin 0

        host.SyncGhosts();                                        // no-op

        Assert.Equal(0f, host.OverlapMargin, 4);
        host.TryGetCell(new CellCoord(1, 0), out CellSim b);
        Assert.Equal(0, b.GhostCount);
    }
}
