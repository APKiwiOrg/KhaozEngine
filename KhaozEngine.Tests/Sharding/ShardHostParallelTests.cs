using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// jobs-1 acceptance: ticking a <see cref="ShardHost"/>'s independent cells across cores must produce world state
/// identical to the single-threaded tick (cells are disjoint <see cref="World"/>s), with the parallelism strictly
/// opt-in (default scheduler is inline).
/// </summary>
public class ShardHostParallelTests
{
    private struct Tally : IComponent { public int Value; }

    private sealed class IncrementSystem : ISystem
    {
        public void Update(World world, float dt) =>
            world.ForEach<Tally>((Entity _, ref Tally t) => t.Value++);
    }

    private static ShardHost BuildGrid(int gw, int gh, int e, ulong seed, IJobScheduler? sched)
    {
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, registry: new ReplicationRegistry());
        if (sched is not null) host.Scheduler = sched;
        var rng = new DeterministicRng(seed);
        for (int cy = 0; cy < gh; cy++)
        for (int cx = 0; cx < gw; cx++)
            for (int i = 0; i < e; i++)
            {
                float x = cx * 100f + 5f + rng.NextFloat() * 90f;
                float y = cy * 100f + 5f + rng.NextFloat() * 90f;
                Entity ent = host.SpawnAt(x, y, out CellSim cell);
                cell.World.Set(ent, new Tally { Value = (int)(rng.NextULong() & 0xFFFF) });
            }
        foreach (CellSim cell in host.Cells) cell.World.AddSystem(new IncrementSystem());
        return host;
    }

    private static List<int> TallySnapshot(ShardHost host)
    {
        var vals = new List<int>();
        foreach (CellSim cell in host.Cells)
            cell.World.ForEach<Tally>((Entity _, ref Tally t) => vals.Add(t.Value));
        return vals;
    }

    [Fact]
    public void DefaultScheduler_IsSingleThreaded()
    {
        var host = new ShardHost(100f, 0.1f, new ReplicationRegistry());
        Assert.IsType<SingleThreadedJobScheduler>(host.Scheduler);
    }

    [Fact]
    public void Scheduler_NullAssignment_Throws()
    {
        var host = new ShardHost(100f, 0.1f, new ReplicationRegistry());
        Assert.Throws<ArgumentNullException>(() => host.Scheduler = null!);
    }

    [Fact]
    public void ParallelTick_ProducesIdenticalStateToSingleThreaded()
    {
        const ulong seed = 0xBEEFUL;
        ShardHost inline = BuildGrid(8, 8, 50, seed, new SingleThreadedJobScheduler());
        ShardHost parallel = BuildGrid(8, 8, 50, seed, new ThreadPoolJobScheduler());

        for (int k = 0; k < 25; k++)
        {
            inline.Tick(0.1f, maxTicksPerFrame: 1);
            parallel.Tick(0.1f, maxTicksPerFrame: 1);
        }

        Assert.Equal(TallySnapshot(inline), TallySnapshot(parallel));   // same values, same order

        var inlineCells = new List<CellSim>(inline.Cells);
        var parallelCells = new List<CellSim>(parallel.Cells);
        Assert.Equal(inlineCells.Count, parallelCells.Count);
        for (int i = 0; i < inlineCells.Count; i++)
            Assert.Equal(inlineCells[i].TickCount, parallelCells[i].TickCount);
    }

    [Fact]
    public void ParallelTick_ManyCells_NoLostUpdates_Repeatable()
    {
        for (int run = 0; run < 3; run++)
        {
            ShardHost host = BuildGrid(20, 20, 25, seed: (ulong)(run + 1), sched: new ThreadPoolJobScheduler());
            List<int> before = TallySnapshot(host);
            const int ticks = 40;
            for (int k = 0; k < ticks; k++) host.Tick(0.1f, maxTicksPerFrame: 1);
            List<int> after = TallySnapshot(host);

            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
                Assert.Equal(before[i] + ticks, after[i]);   // exactly one increment per tick, none lost or torn
        }
    }

    [Fact]
    public void Tick_RoutesThroughScheduler_OncePerTick_WithCellCount()
    {
        var recorder = new RecordingScheduler();
        ShardHost host = BuildGrid(3, 4, 2, seed: 7UL, sched: recorder);   // 12 cells

        host.Tick(0.1f, maxTicksPerFrame: 1);
        host.Tick(0.1f, maxTicksPerFrame: 1);

        Assert.Equal(2, recorder.ForCalls);
        Assert.Equal(host.CellCount, recorder.LastCount);
        Assert.Equal(12, recorder.LastCount);
    }

    [Fact]
    public void EmptyHost_Tick_DoesNotInvokeScheduler()
    {
        var recorder = new RecordingScheduler();
        var host = new ShardHost(100f, 0.1f, new ReplicationRegistry()) { Scheduler = recorder };
        host.Tick(0.1f);
        Assert.Equal(0, recorder.ForCalls);
    }

    private sealed class RecordingScheduler : IJobScheduler
    {
        public int ForCalls { get; private set; }
        public int LastCount { get; private set; }

        public void For(int count, Action<int> body)
        {
            ForCalls++;
            LastCount = count;
            for (int i = 0; i < count; i++) body(i);
        }
    }
}
