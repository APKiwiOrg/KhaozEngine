using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The Sharding half of cell eviction: <see cref="ShardHost.RemoveCell"/> is the mechanical removal that runs
/// AFTER a persist gate at the NetWorld level (see <c>CellEvictor</c>). These tests pin what removal must clean up
/// (the cell map, the creation-ordered list, the host ownership index, neighbour ghost views, the reused tick
/// buffer) and what it must refuse (a cell mid-handoff, a cell holding a bound client's player, a cell with
/// undrained inter-cell traffic).
/// </summary>
public class ShardHostEvictionTests
{
    private struct Pos : IComponent { public float X; public float Y; }
    private struct Hp : IComponent { public int Value; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Hp>(2, (h, bw) => bw.Write(h.Value), br => new Hp { Value = br.ReadInt32() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static ShardHost Host(float overlapMargin = 0f) =>
        new(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin, positionAccessor: PosAccessor);

    private static Entity Spawn(ShardHost host, long netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnOwned(x, y, netId, out cell);
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    [Fact]
    public void RemoveCell_DropsTheCell_AndEveryOwnerIndexEntryPointingAtIt()
    {
        ShardHost host = Host();
        Spawn(host, 1, 50f, 50f, out CellSim a);
        Spawn(host, 2, 50f, 60f, out _);
        Spawn(host, 3, 150f, 50f, out _);   // a second cell, untouched by the eviction

        Assert.True(host.RemoveCell(a.Coord));

        Assert.False(host.TryGetCell(a.Coord, out _));
        Assert.Equal(1, host.CellCount);
        Assert.False(host.TryGetOwner(1, out _, out _));
        Assert.False(host.TryGetOwner(2, out _, out _));
        foreach (KeyValuePair<long, CellCoord> kv in host.OwnerCellEntries)
            Assert.NotEqual(a.Coord, kv.Value);          // no stale entry survives the eviction
        Assert.True(host.TryGetOwner(3, out _, out _));  // the other cell is unaffected
    }

    [Fact]
    public void RemoveCell_ClearsTheGhostsNeighboursHoldFromIt()
    {
        ShardHost host = Host(overlapMargin: 20f);
        host.CellFor(150f, 50f);                     // B = (1,0) exists to receive a ghost
        Spawn(host, 7, 90f, 50f, out CellSim a);     // owned by A = (0,0), inside the east border margin
        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(b.TryGetGhost(7, out _));

        Assert.True(host.RemoveCell(a.Coord));

        Assert.False(b.TryGetGhost(7, out _));                // the mirrored ghost is gone
        Assert.Equal(0, b.GhostCount);
        Assert.DoesNotContain(a.Coord, b.GhostSources);       // and so is the view keyed on the dead source
    }

    [Fact]
    public void RemoveCell_RefusesWhileAnEntityIsMigratingOut()
    {
        ShardHost host = Host();
        Entity e = Spawn(host, 5, 50f, 50f, out CellSim a);
        a.World.Set(e, new Migrating { Destination = new CellCoord(1, 0) });

        Assert.False(host.CanRemoveCell(a.Coord));
        Assert.False(host.RemoveCell(a.Coord));
        Assert.True(host.TryGetCell(a.Coord, out _));
    }

    [Fact]
    public void RemoveCell_RefusesWhileInterCellTrafficIsQueuedForIt()
    {
        ShardHost host = Host();
        host.CellFor(50f, 50f);
        var coord = new CellCoord(0, 0);
        host.CellLink.Send(new CellMessage(new CellCoord(1, 0), coord, CellMessageKind.Migrate, new byte[] { 0, 0, 0, 0 }));

        Assert.False(host.CanRemoveCell(coord));
        Assert.False(host.RemoveCell(coord));
    }

    [Fact]
    public void RemoveCell_RefusesACellHoldingABoundClientsPlayer()
    {
        ShardHost host = Host();
        Spawn(host, 42, 50f, 50f, out CellSim a);
        host.BindClient(slot: 0, playerNetId: 42);

        Assert.False(host.CanRemoveCell(a.Coord));
        Assert.False(host.RemoveCell(a.Coord));

        host.UnbindClient(0);
        Assert.True(host.RemoveCell(a.Coord));
    }

    [Fact]
    public void RemoveCell_RaisesCellRemoved_AndRecreationRaisesCellCreatedAgain()
    {
        ShardHost host = Host();
        var created = new List<CellCoord>();
        var removed = new List<CellCoord>();
        host.CellCreated += c => created.Add(c.Coord);
        host.CellRemoved += c => removed.Add(c.Coord);

        CellSim a = host.EnsureCell(new CellCoord(0, 0));
        Assert.Equal(new[] { new CellCoord(0, 0) }, created);

        Assert.True(host.RemoveCell(a.Coord));
        Assert.Equal(new[] { new CellCoord(0, 0) }, removed);

        CellSim again = host.EnsureCell(new CellCoord(0, 0));
        Assert.Equal(2, created.Count);                 // the load hook fires again on recreation
        Assert.NotSame(a, again);                       // a genuinely fresh cell, not the evicted one
    }

    [Fact]
    public void Tick_AfterEvictingOneCellAndCreatingAnother_TicksTheNewCellNotTheEvictedOne()
    {
        // The reused tick buffer used to be refreshed only when the cell COUNT changed, which was sound while the
        // cell list was append-only. Evict one and create one in the same frame and the count is unchanged, so a
        // count-keyed buffer would keep ticking the evicted cell and never tick the new one.
        ShardHost host = Host();
        CellSim a = host.EnsureCell(new CellCoord(0, 0));
        host.Tick(0.1f);
        Assert.Equal(1, a.TickCount);

        Assert.True(host.RemoveCell(a.Coord));
        CellSim b = host.EnsureCell(new CellCoord(5, 5));
        Assert.Equal(1, host.CellCount);

        host.Tick(0.1f);
        Assert.Equal(1, b.TickCount);   // the new cell advanced
        Assert.Equal(1, a.TickCount);   // the evicted one did not
    }

    [Fact]
    public void RemoveCell_IsRefusedForACoordThatWasNeverInstantiated()
    {
        ShardHost host = Host();
        Assert.False(host.CanRemoveCell(new CellCoord(9, 9)));
        Assert.False(host.RemoveCell(new CellCoord(9, 9)));
    }

    [Fact]
    public void CollectBoundPlayerCells_ReportsTheHomeCellOfEveryBoundClient()
    {
        ShardHost host = Host();
        Spawn(host, 10, 50f, 50f, out _);
        Spawn(host, 11, 250f, 50f, out _);
        host.BindClient(0, 10);
        host.BindClient(1, 11);
        host.BindClient(2, 99);   // bound to a player no cell owns: contributes nothing

        var cells = new List<CellCoord>();
        host.CollectBoundPlayerCells(cells);

        Assert.Equal(2, cells.Count);
        Assert.Contains(new CellCoord(0, 0), cells);
        Assert.Contains(new CellCoord(2, 0), cells);
    }

    [Fact]
    public void IdleCellEvictionPolicy_EvictsAnIdleUnboundCell_AndKeepsAnActiveOne()
    {
        var policy = new IdleCellEvictionPolicy { IdleSeconds = 60f, KeepRadius = 2 };
        var coord = new CellCoord(10, 10);

        // Idle long enough, no players homed here, nearest player far away: evictable.
        Assert.True(policy.ShouldEvict(new CellEvictionSignals(coord, ownedEntityCount: 3, boundPlayerCount: 0,
            cellsToNearestBoundPlayer: 5, pinned: false, idleSeconds: 90f)));

        // A player is homed in it.
        Assert.False(policy.ShouldEvict(new CellEvictionSignals(coord, 3, boundPlayerCount: 1,
            cellsToNearestBoundPlayer: 0, pinned: false, idleSeconds: 90f)));

        // Idle, but a player is inside the keep radius.
        Assert.False(policy.ShouldEvict(new CellEvictionSignals(coord, 3, 0,
            cellsToNearestBoundPlayer: 2, pinned: false, idleSeconds: 90f)));

        // Far from anyone but not idle long enough yet.
        Assert.False(policy.ShouldEvict(new CellEvictionSignals(coord, 3, 0, 5, pinned: false, idleSeconds: 30f)));

        // Pinned by the host.
        Assert.False(policy.ShouldEvict(new CellEvictionSignals(coord, 3, 0, 5, pinned: true, idleSeconds: 90f)));

        // Nobody online at all: the whole world is evictable.
        Assert.True(policy.ShouldEvict(new CellEvictionSignals(coord, 3, 0,
            cellsToNearestBoundPlayer: int.MaxValue, pinned: false, idleSeconds: 90f)));
    }
}
