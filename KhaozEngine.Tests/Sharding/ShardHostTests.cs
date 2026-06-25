using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostTests
{
    private static ShardHost NewHost(float cellSize = 100f, float tickSeconds = 0.1f) =>
        new(cellSize, tickSeconds, new ReplicationRegistry());

    private static int CountNetIds(World world)
    {
        int n = 0;
        world.ForEach<NetId>((Entity _, ref NetId _) => n++);
        return n;
    }

    [Fact]
    public void CellFor_CreatesOnDemand_AndReturnsSameInstancePerCell()
    {
        var host = NewHost();

        CellSim a = host.CellFor(5f, 5f);          // cell (0,0)
        CellSim aAgain = host.CellFor(50f, 50f);   // same cell (0,0)
        CellSim b = host.CellFor(150f, 5f);        // cell (1,0)

        Assert.Same(a, aAgain);
        Assert.NotSame(a, b);
        Assert.Equal(new CellCoord(0, 0), a.Coord);
        Assert.Equal(new CellCoord(1, 0), b.Coord);
        Assert.Equal(2, host.CellCount);
        Assert.Equal(2, host.Cells.Count);
    }

    [Fact]
    public void CoordFor_FloorsPosition_WithoutCreatingACell()
    {
        var host = NewHost();

        Assert.Equal(new CellCoord(-1, 2), host.CoordFor(-50f, 250f));
        Assert.Equal(0, host.CellCount);   // pure query, no cell created
    }

    [Fact]
    public void TryGetCell_DoesNotCreate()
    {
        var host = NewHost();

        Assert.False(host.TryGetCell(new CellCoord(0, 0), out _));
        host.CellFor(5f, 5f);
        Assert.True(host.TryGetCell(new CellCoord(0, 0), out CellSim cell));
        Assert.Equal(new CellCoord(0, 0), cell.Coord);
    }

    [Fact]
    public void Tick_AdvancesEveryCellsFixedTickHost_Deterministically()
    {
        var host = NewHost(cellSize: 100f, tickSeconds: 0.1f);
        CellSim c1 = host.CellFor(5f, 5f);
        CellSim c2 = host.CellFor(150f, 5f);

        host.Tick(0.25f);   // 2 whole ticks, 0.05 left over (every cell)
        host.Tick(0.05f);   // + 1 tick

        Assert.Equal(3L, c1.TickCount);
        Assert.Equal(3L, c2.TickCount);
    }

    [Fact]
    public void SpawnAt_RoutesEntitiesToTheCorrectCellWorld()
    {
        var host = NewHost(cellSize: 100f);

        Entity e1 = host.SpawnAt(5f, 5f, out CellSim c1);        // cell (0,0)
        Entity e2 = host.SpawnAt(150f, 5f, out CellSim c2);      // cell (1,0)
        Entity e3 = host.SpawnAt(60f, 60f, out CellSim c3);      // also cell (0,0)

        Assert.Equal(new CellCoord(0, 0), c1.Coord);
        Assert.Equal(new CellCoord(1, 0), c2.Coord);
        Assert.Same(c1, c3);                                     // same cell -> same world
        Assert.NotSame(c1, c2);

        Assert.True(c1.World.IsAlive(e1));
        Assert.True(c1.World.IsAlive(e3));
        Assert.True(c2.World.IsAlive(e2));

        // Two entities landed in c1's world, exactly one in c2's.
        c1.World.Set(e1, new NetId(1));
        c1.World.Set(e3, new NetId(3));
        c2.World.Set(e2, new NetId(2));
        Assert.Equal(2, CountNetIds(c1.World));
        Assert.Equal(1, CountNetIds(c2.World));
    }

    [Fact]
    public void CellSize_IsExposed()
    {
        var host = NewHost(cellSize: 250f);
        Assert.Equal(250f, host.CellSize, 4);
    }
}
