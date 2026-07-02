using System.Collections.Generic;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostCellCreatedTests
{
    private static ShardHost Host() => new(cellSize: 10f, tickSeconds: 1f / 30f, registry: new ReplicationRegistry());

    [Fact]
    public void CellCreated_FiresOncePerNewCoord_NotOnRepeat()
    {
        ShardHost host = Host();
        var fired = new List<CellCoord>();
        host.CellCreated += c => fired.Add(c.Coord);

        host.CellFor(5f, 5f);                 // creates cell (0,0)
        host.SpawnAt(5f, 5f, out _);          // same cell (0,0) - no new fire
        host.EnsureCell(new CellCoord(3, 0)); // creates cell (3,0)
        host.EnsureCell(new CellCoord(3, 0)); // existing - no new fire

        Assert.Equal(new[] { new CellCoord(0, 0), new CellCoord(3, 0) }, fired);
    }

    [Fact]
    public void EnsureCell_ReturnsSameInstanceForSameCoord()
    {
        ShardHost host = Host();
        CellSim a = host.EnsureCell(new CellCoord(2, 2));
        CellSim b = host.EnsureCell(new CellCoord(2, 2));
        Assert.Same(a, b);
    }
}
