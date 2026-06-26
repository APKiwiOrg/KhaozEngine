using System.Collections.Generic;
using KhaozEngine.Benchmarks;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// Headless acceptance for the jobs-0 server-tick benchmark harness. The timing loop itself is not unit-tested
/// (wall-clock is observational, run via <c>dotnet run</c>); what is asserted here is the harness's deterministic
/// and structural behaviour - the things that make the reported numbers trustworthy and repeatable.
/// </summary>
public class ServerTickBenchmarkTests
{
    private static BenchmarkConfig SmallConfig(int gw = 4, int gh = 2, int e = 5, int s = 2,
        ulong seed = 0xC0FFEEUL, int warmup = 0, int timed = 3) => new()
    {
        Name = "test",
        GridWidth = gw,
        GridHeight = gh,
        EntitiesPerCell = e,
        Systems = s,
        WarmupTicks = warmup,
        TimedTicks = timed,
        Seed = seed,
    };

    private static List<(float px, float py, float vx, float vy)> Snapshot(ShardHost host)
    {
        var rows = new List<(float, float, float, float)>();
        foreach (CellSim cell in host.Cells)
            cell.World.ForEach<BenchPosition, BenchVelocity>((Entity _, ref BenchPosition p, ref BenchVelocity v) =>
                rows.Add((p.X, p.Y, v.X, v.Y)));
        return rows;
    }

    [Fact]
    public void Build_CreatesExpectedCellAndEntityCounts()
    {
        BenchmarkConfig config = SmallConfig(gw: 4, gh: 2, e: 5);
        ShardHost host = ServerTickBenchmark.Build(config);

        Assert.Equal(8, host.CellCount);                 // 4 × 2 = C
        Assert.Equal(40, Snapshot(host).Count);          // C · E entities, each with both components
        Assert.Equal(40L, config.TotalEntities);
    }

    [Fact]
    public void Build_EntitiesLandInTheCellTheyWereRoutedTo()
    {
        // Each populated entity's BenchPosition must fall inside the world bounds of the cell that owns it.
        BenchmarkConfig config = SmallConfig(gw: 3, gh: 3, e: 7);
        ShardHost host = ServerTickBenchmark.Build(config);
        float s = config.CellSize;

        foreach (CellSim cell in host.Cells)
        {
            float minX = cell.Coord.X * s, minY = cell.Coord.Y * s;
            cell.World.ForEach<BenchPosition, BenchVelocity>((Entity _, ref BenchPosition p, ref BenchVelocity _) =>
            {
                Assert.InRange(p.X, minX, minX + s);
                Assert.InRange(p.Y, minY, minY + s);
            });
        }
    }

    [Fact]
    public void Build_IsDeterministic_SameSeedYieldsIdenticalPopulation()
    {
        BenchmarkConfig config = SmallConfig(seed: 12345UL);

        List<(float, float, float, float)> a = Snapshot(ServerTickBenchmark.Build(config));
        List<(float, float, float, float)> b = Snapshot(ServerTickBenchmark.Build(config));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Build_DifferentSeedYieldsDifferentPopulation()
    {
        List<(float, float, float, float)> a = Snapshot(ServerTickBenchmark.Build(SmallConfig(seed: 1UL)));
        List<(float, float, float, float)> b = Snapshot(ServerTickBenchmark.Build(SmallConfig(seed: 2UL)));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IntegrateSystem_AdvancesPositionByVelocityTimesDt()
    {
        // One system, one tick: pos must advance by exactly vel·dt (a single float addition, so bit-exact).
        BenchmarkConfig config = SmallConfig(gw: 2, gh: 2, e: 3, s: 1, timed: 0);
        ShardHost host = ServerTickBenchmark.Build(config);
        float dt = config.TickSeconds;

        List<(float px, float py, float vx, float vy)> before = Snapshot(host);
        host.Tick(dt, maxTicksPerFrame: 1);
        List<(float px, float py, float vx, float vy)> after = Snapshot(host);

        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].px + before[i].vx * dt, after[i].px);
            Assert.Equal(before[i].py + before[i].vy * dt, after[i].py);
            Assert.Equal(before[i].vx, after[i].vx);   // velocity untouched
            Assert.Equal(before[i].vy, after[i].vy);
        }
    }

    [Fact]
    public void Build_RegistersSystemsPerCell_SoWorkScalesWithS()
    {
        // With S systems, one tick advances position by S · vel·dt (S integrate passes).
        BenchmarkConfig config = SmallConfig(gw: 1, gh: 1, e: 4, s: 3, timed: 0);
        ShardHost host = ServerTickBenchmark.Build(config);
        float dt = config.TickSeconds;

        List<(float px, float py, float vx, float vy)> before = Snapshot(host);
        host.Tick(dt, maxTicksPerFrame: 1);
        List<(float px, float py, float vx, float vy)> after = Snapshot(host);

        for (int i = 0; i < before.Count; i++)
        {
            float expected = before[i].px;
            for (int s = 0; s < config.Systems; s++) expected += before[i].vx * dt;   // replay S additions, bit-exact
            Assert.Equal(expected, after[i].px);
        }
    }

    [Fact]
    public void Run_MeasuresEveryTimedTick_AndReportsThePopulation()
    {
        BenchmarkConfig config = SmallConfig(gw: 4, gh: 4, e: 64, s: 2, warmup: 2, timed: 10);
        BenchmarkResult result = ServerTickBenchmark.Run(config);

        Assert.Equal(config.CellCount, result.CellCount);
        Assert.Equal(config.TotalEntities, result.TotalEntities);
        Assert.Equal(config.TimedTicks, result.TicksMeasured);   // each ShardHost.Tick = exactly one fixed tick
        Assert.True(result.ElapsedMs >= 0.0);
        Assert.True(result.PerTickMs >= 0.0);
        Assert.True(result.EntitiesPerSecond > 0.0);
        Assert.Equal(result.EntitiesPerSecond * config.Systems, result.ComponentVisitsPerSecond, 3);
    }

    [Fact]
    public void DefaultMatrix_CoversManyCells_OneHotCell_AndMid_AtEqualTotalEntities()
    {
        IReadOnlyList<BenchmarkConfig> matrix = BenchmarkMatrix.Default();

        Assert.Contains(matrix, c => c.CellCount > 1 && c.EntitiesPerCell < c.CellCount); // many small cells
        Assert.Contains(matrix, c => c.CellCount == 1);                                    // one hot cell
        Assert.Contains(matrix, c => c.CellCount > 1 && c.CellCount < 1000);               // a mid case

        long n = matrix[0].TotalEntities;
        Assert.All(matrix, c => Assert.Equal(n, c.TotalEntities));                         // comparable across regimes
    }
}
