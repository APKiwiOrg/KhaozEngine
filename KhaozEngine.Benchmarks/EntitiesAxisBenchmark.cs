using System;
using System.Diagnostics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Simulation;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// jobs-2 entities-axis measurement: one hot <see cref="World"/> of many entities, its dominant per-entity pass run
/// single-threaded (<see cref="World.ForEach{T1,T2}(RefAction{T1,T2})"/>) versus fanned across cores
/// (<see cref="World.ParallelForEach{T1,T2}(RefAction{T1,T2}, IJobScheduler?)"/>). This is the single-hot-cell case
/// the cell axis (jobs-1) cannot speed up - one cell can't be split across cores, but its <em>rows</em> can.
/// </summary>
/// <remarks>
/// Sweeping the per-row work (<c>workPerRow</c>) is the whole point: a fork/join has a fixed cost, so over trivial
/// per-row work (a couple of flops) the dispatch overhead dominates and the parallel pass <em>loses</em>. As the
/// per-row work grows toward what a real hot system does (AI, steering, spatial math), the compute amortizes the
/// fork/join and the parallel pass scales toward ~P×. The benchmark prints the crossover so the win is never claimed
/// where it isn't real. The integrate is per-row-pure and bounded (the <c>*0.9999f</c> decay keeps it from blowing
/// up), so inline and parallel compute bit-identically regardless of how rows partition.
/// </remarks>
public static class EntitiesAxisBenchmark
{
    /// <summary>One swept point: inline vs parallel wall-clock per pass at a given <see cref="WorkPerRow"/>.</summary>
    public sealed class Point
    {
        public required int WorkPerRow { get; init; }
        public required double InlineMs { get; init; }
        public required double ParMs { get; init; }
        public double Speedup => ParMs > 0 ? InlineMs / ParMs : 0;
    }

    /// <summary>
    /// Builds a hot World of <paramref name="entities"/> seeded entities and times one integrate pass over it both
    /// ways (inline ForEach, then ParallelForEach on <paramref name="scheduler"/>), each after
    /// <paramref name="warmup"/> un-timed passes, averaged over <paramref name="timed"/> passes. The two passes run
    /// on separate but identically-seeded worlds so each starts from the same state.
    /// </summary>
    public static Point Measure(int entities, int workPerRow, int warmup, int timed, IJobScheduler scheduler,
        ulong seed = 0xC0FFEEUL)
    {
        double inlineMs = TimePasses(Build(entities, seed), workPerRow, warmup, timed, scheduler: null);
        double parMs = TimePasses(Build(entities, seed), workPerRow, warmup, timed, scheduler);
        return new Point { WorkPerRow = workPerRow, InlineMs = inlineMs, ParMs = parMs };
    }

    private static double TimePasses(World w, int work, int warmup, int timed, IJobScheduler? scheduler)
    {
        const float dt = 1f / 30f;
        Action pass = scheduler is null
            ? () => w.ForEach<BenchPosition, BenchVelocity>((Entity _, ref BenchPosition p, ref BenchVelocity v) =>
                Integrate(ref p, ref v, dt, work))
            : () => w.ParallelForEach<BenchPosition, BenchVelocity>((Entity _, ref BenchPosition p, ref BenchVelocity v) =>
                Integrate(ref p, ref v, dt, work), scheduler);

        for (int i = 0; i < warmup; i++) pass();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < timed; i++) pass();
        sw.Stop();
        return timed <= 0 ? 0 : sw.Elapsed.TotalMilliseconds / timed;
    }

    // The per-row work: an integrate repeated `work` times with a decay so the result stays bounded and the JIT
    // can't fold the loop away. Per-row-pure (only this entity's components), so order-independent across threads.
    private static void Integrate(ref BenchPosition p, ref BenchVelocity v, float dt, int work)
    {
        float x = p.X, y = p.Y;
        for (int k = 0; k < work; k++)
        {
            x = (x + v.X * dt) * 0.9999f;
            y = (y + v.Y * dt) * 0.9999f;
        }
        p.X = x;
        p.Y = y;
    }

    private static World Build(int entities, ulong seed)
    {
        var w = new World();
        var rng = new DeterministicRng(seed);
        for (int i = 0; i < entities; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new BenchPosition { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            w.Set(e, new BenchVelocity { X = (rng.NextFloat() - 0.5f) * 2f, Y = (rng.NextFloat() - 0.5f) * 2f });
        }
        return w;
    }
}
