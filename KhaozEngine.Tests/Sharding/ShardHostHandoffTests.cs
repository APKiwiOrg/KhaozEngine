using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostHandoffTests
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

    // Pure authority handoff (no ghosting): cellSize 100, margin 0, position from Pos.
    private static ShardHost HandoffHost(ReplicationRegistry registry) =>
        new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin: 0f, positionAccessor: PosAccessor);

    private static Entity SpawnOwned(ShardHost host, int netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnAt(x, y, out cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static void MoveOwner(ShardHost host, int netId, float x, float y)
    {
        Assert.True(host.TryGetOwner(netId, out CellSim owner, out Entity e));
        owner.World.Set(e, new Pos { X = x, Y = y });
    }

    [Fact]
    public void StepwiseCrossing_OwnedByExactlyOneCell_AtEveryTick()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        SpawnOwned(host, 7, 50f, 50f, out CellSim a);
        Assert.Equal(new CellCoord(0, 0), a.Coord);
        Assert.Equal(1, host.OwnerCount(7));

        foreach (float x in new[] { 70f, 90f, 110f, 130f, 150f }) // crosses A(0,0) -> B(1,0) at x=100
        {
            MoveOwner(host, 7, x, 50f);
            host.ProcessHandoffs();
            Assert.Equal(1, host.OwnerCount(7));                 // never 0 (loss) or 2 (duplication)
        }

        Assert.True(host.TryGetOwner(7, out CellSim final, out _));
        Assert.Equal(new CellCoord(1, 0), final.Coord);         // ended owned by B
    }

    [Fact]
    public void Handoff_PreservesComponentState_AndNetId()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        Entity e = SpawnOwned(host, 7, 95f, 50f, out CellSim a);
        a.World.Set(e, new Hp { Value = 42 });

        MoveOwner(host, 7, 130f, 60f);                          // cross into B
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim b, out Entity moved));
        Assert.Equal(new CellCoord(1, 0), b.Coord);
        Assert.Equal(7, b.World.Get<NetId>(moved).Value);       // NetId unchanged
        Assert.Equal(42, b.World.Get<Hp>(moved).Value);         // full component set preserved
        Assert.Equal(130f, b.World.Get<Pos>(moved).X);
        Assert.Equal(60f, b.World.Get<Pos>(moved).Y);
    }

    [Fact]
    public void RapidBackAndForth_NeverDuplicatesOrDrops()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        SpawnOwned(host, 7, 95f, 50f, out _);

        foreach (float x in new[] { 110f, 90f, 130f, 40f, 150f, 95f, 220f }) // A<->B<->C back and forth
        {
            MoveOwner(host, 7, x, 50f);
            host.ProcessHandoffs();
            Assert.Equal(1, host.OwnerCount(7));
        }

        Assert.True(host.TryGetOwner(7, out CellSim owner, out _));
        Assert.Equal(new CellCoord(2, 0), owner.Coord);         // x=220 -> cell (2,0)
    }

    [Fact]
    public void Handoff_CreatesDestinationCell_IfMissing()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        SpawnOwned(host, 7, 95f, 50f, out _);
        Assert.Equal(1, host.CellCount);
        Assert.False(host.TryGetCell(new CellCoord(1, 0), out _));

        MoveOwner(host, 7, 130f, 50f);                          // cross into not-yet-existing B
        host.ProcessHandoffs();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(host.TryGetOwner(7, out CellSim owner, out _));
        Assert.Equal(b.Coord, owner.Coord);
    }

    [Fact]
    public void NoCrossing_NoHandoff()
    {
        ReplicationRegistry registry = Registry();
        ShardHost host = HandoffHost(registry);
        SpawnOwned(host, 7, 50f, 50f, out CellSim a);

        MoveOwner(host, 7, 60f, 40f);                           // still inside A
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim owner, out _));
        Assert.Equal(a.Coord, owner.Coord);
        Assert.Equal(1, host.CellCount);                       // no spurious destination cell
    }

    [Fact]
    public void ProcessHandoffs_WithoutPositionAccessor_Throws()
    {
        var host = new ShardHost(100f, 0.1f, new ReplicationRegistry(), interestCellSize: 100f, overlapMargin: 0f);
        Assert.Throws<InvalidOperationException>(() => host.ProcessHandoffs());
    }

    [Fact]
    public void Handoff_WhenDestinationHeldAGhost_LeavesExactlyOneOwnedCopy()
    {
        ReplicationRegistry registry = Registry();
        // ghosting ON so B holds a ghost of the entity before it crosses.
        var host = new ShardHost(100f, 0.1f, registry, interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor);
        host.CellFor(150f, 50f);                                // B=(1,0) exists
        SpawnOwned(host, 7, 90f, 50f, out _);                  // within 20 of east edge
        host.SyncGhosts();
        host.TryGetCell(new CellCoord(1, 0), out CellSim b);
        Assert.True(b.TryGetGhost(7, out _));                  // B has a ghost
        Assert.Equal(1, host.OwnerCount(7));                   // owned by A

        MoveOwner(host, 7, 110f, 50f);                         // cross into B
        host.ProcessHandoffs();

        Assert.Equal(1, host.OwnerCount(7));                  // exactly one owner, not duped with the ghost
        Assert.True(host.TryGetOwner(7, out CellSim owner, out _));
        Assert.Equal(new CellCoord(1, 0), owner.Coord);
        Assert.False(b.TryGetGhost(7, out _));               // the ghost was adopted into the owned copy
        Assert.Equal(0, b.GhostCount);
    }
}
