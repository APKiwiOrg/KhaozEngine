using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellSimTests
{
    private sealed class CountingSystem : ISystem
    {
        public int Ticks;
        public float LastDt;
        public void Update(World world, float dt) { Ticks++; LastDt = dt; }
    }

    private static CellSim NewCell(CellCoord coord, float tickSeconds) =>
        new(coord, tickSeconds, new ReplicationRegistry(), interestCellSize: 10f);

    [Fact]
    public void ExposesCoordAndOwnedSubsystems()
    {
        var cell = NewCell(new CellCoord(2, -3), tickSeconds: 1f / 30f);

        Assert.Equal(new CellCoord(2, -3), cell.Coord);
        Assert.NotNull(cell.World);
        Assert.NotNull(cell.Replicator);
        Assert.NotNull(cell.Interest);
        Assert.Equal(1f / 30f, cell.TickSeconds, 6);
        Assert.Equal(0L, cell.TickCount);
    }

    [Fact]
    public void Tick_AdvancesFixedTickHost_Deterministically()
    {
        var cell = NewCell(new CellCoord(0, 0), tickSeconds: 0.1f);

        Assert.Equal(2, cell.Tick(0.25f));   // 0.25s -> 2 whole ticks, 0.05 left over
        Assert.Equal(2L, cell.TickCount);
        Assert.Equal(1, cell.Tick(0.05f));   // 0.05 + 0.05 = 0.10 -> 1 tick
        Assert.Equal(3L, cell.TickCount);
        Assert.Equal(0, cell.Tick(0.05f));   // 0.05 left over, no whole tick
        Assert.Equal(3L, cell.TickCount);
    }

    [Fact]
    public void Tick_StepsEcsSystems_OncePerFixedTick_WithTickDt()
    {
        const float dt = 0.1f;
        var cell = NewCell(new CellCoord(0, 0), tickSeconds: dt);
        var system = new CountingSystem();
        cell.World.AddSystem(system);

        cell.Tick(0.3f);   // exactly 3 fixed ticks

        Assert.Equal(3, system.Ticks);
        Assert.Equal(dt, system.LastDt, 5);
    }

    [Fact]
    public void Replicator_CapturesTheCellsOwnWorld()
    {
        var cell = NewCell(new CellCoord(0, 0), tickSeconds: 0.1f);
        Entity e = cell.World.Spawn();
        cell.World.Set(e, new NetId(42));

        int seq = cell.Replicator.Capture(cell.World);

        Assert.Equal(1, seq);                       // first capture is seq 1
        Assert.Equal(1, cell.Replicator.CurrentSeq);
    }
}
