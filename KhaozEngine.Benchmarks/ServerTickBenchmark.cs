using System.Diagnostics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// Stands up a <c>ShardHost</c> from a <see cref="BenchmarkConfig"/>, populates it deterministically, and times
/// the single-threaded server tick. This is jobs-0 of the parallel-job-system program: the measurement every
/// later layer (parallel cell ticks, parallel ForEach, the system scheduler) is justified against. Headless and
/// repeatable - <see cref="Build"/>'s population is seeded, so a config's timings are stable run to run.
/// </summary>
public static class ServerTickBenchmark
{
    /// <summary>
    /// Builds a fully-populated <c>ShardHost</c> for <paramref name="config"/>: a
    /// <see cref="BenchmarkConfig.CellCount"/> grid, <see cref="BenchmarkConfig.EntitiesPerCell"/> seeded entities
    /// per cell (each with <see cref="BenchPosition"/> + <see cref="BenchVelocity"/>), and
    /// <see cref="BenchmarkConfig.Systems"/> <see cref="IntegratePositionSystem"/>s per cell. No ticks are run.
    /// Deterministic: the same config (same <see cref="BenchmarkConfig.Seed"/>) produces the same world state every
    /// time, so timings are repeatable. Ghosting/handoff are never invoked - this is the pure owned-tick path.
    /// </summary>
    public static ShardHost Build(BenchmarkConfig config)
    {
        // An empty registry is enough: the benchmark never snapshots/ghosts/hands-off, it only ticks owned cells.
        var host = new ShardHost(config.CellSize, config.TickSeconds, new ReplicationRegistry());
        var rng = new DeterministicRng(config.Seed);
        float s = config.CellSize;

        // Populate in row-major order so cell creation order (and thus ShardHost.Cells iteration) is stable, and
        // the RNG is consumed in a fixed order - the two facts that make the population bit-reproducible.
        for (int cy = 0; cy < config.GridHeight; cy++)
        for (int cx = 0; cx < config.GridWidth; cx++)
        {
            float baseX = cx * s, baseY = cy * s;
            for (int i = 0; i < config.EntitiesPerCell; i++)
            {
                // Offsets in [0.05, 0.95]·cellSize keep every entity safely inside cell (cx,cy) so it routes there.
                float x = baseX + (0.05f + 0.9f * rng.NextFloat()) * s;
                float y = baseY + (0.05f + 0.9f * rng.NextFloat()) * s;
                Entity e = host.SpawnAt(x, y, out CellSim cell);
                cell.World.Set(e, new BenchPosition { X = x, Y = y });
                cell.World.Set(e, new BenchVelocity { X = (rng.NextFloat() - 0.5f) * 2f, Y = (rng.NextFloat() - 0.5f) * 2f });
            }
        }

        // Register the S systems after population, so every cell exists and gets the full system set.
        foreach (CellSim cell in host.Cells)
            for (int sys = 0; sys < config.Systems; sys++)
                cell.World.AddSystem(new IntegratePositionSystem());

        return host;
    }

    /// <summary>
    /// Builds the host, runs <see cref="BenchmarkConfig.WarmupTicks"/> un-timed ticks (JIT/cache warm), then times
    /// <see cref="BenchmarkConfig.TimedTicks"/> ticks and returns the measurement. Per-tick wall-clock divides the
    /// elapsed time by the ticks <em>actually</em> produced, so float accumulator drift can't skew the figure.
    /// Pass a <paramref name="scheduler"/> (e.g. a <see cref="ThreadPoolJobScheduler"/>) to fan the per-cell ticks
    /// across cores; the default is the inline single-threaded baseline.
    /// </summary>
    public static BenchmarkResult Run(BenchmarkConfig config, IJobScheduler? scheduler = null)
    {
        ShardHost host = Build(config);
        if (scheduler is not null) host.Scheduler = scheduler;
        float dt = config.TickSeconds;

        // maxTicksPerFrame:1 + feeding exactly dt ⇒ each ShardHost.Tick advances every cell by exactly one tick.
        for (int i = 0; i < config.WarmupTicks; i++)
            host.Tick(dt, maxTicksPerFrame: 1);

        long before = RepresentativeTickCount(host);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < config.TimedTicks; i++)
            host.Tick(dt, maxTicksPerFrame: 1);
        sw.Stop();

        long measured = RepresentativeTickCount(host) - before;
        if (measured <= 0) measured = config.TimedTicks; // guard the degenerate (no cells / 0 timed) case

        return new BenchmarkResult
        {
            Config = config,
            CellCount = host.CellCount,
            TotalEntities = config.TotalEntities,
            TicksMeasured = measured,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    // Every cell shares the same accumulator/elapsed sequence, so any cell's tick count is the host's tick count.
    private static long RepresentativeTickCount(ShardHost host)
    {
        foreach (CellSim cell in host.Cells) return cell.TickCount;
        return 0;
    }
}
