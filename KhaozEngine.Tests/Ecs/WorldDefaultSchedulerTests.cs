using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

/// <summary>
/// <see cref="World.DefaultScheduler"/> is the seam a client game wires once (<c>world.DefaultScheduler =
/// App.JobScheduler;</c>) so every subsequent no-scheduler <c>ParallelForEach</c> call fans across cores with no
/// per-call plumbing. It must default to the deterministic single-threaded scheduler (so every existing World
/// stays byte-identical to <c>ForEach</c> until a game opts in), an explicit per-call scheduler must always win
/// over it, and a parallel default must still produce results identical to sequential <c>ForEach</c> for a
/// per-row-pure action.
/// </summary>
[Collection("AllocSensitive")]  // spins up ThreadPoolJobScheduler workers; keep off the parallel test pool so it
public class WorldDefaultSchedulerTests   // never churns the GC while ParallelForEachPoolingTests measures allocations
{
    private struct Pos : IComponent { public float X, Y; }
    private struct Vel : IComponent { public float X, Y; }

    private static World BuildWorld(ulong seed, int n)
    {
        var w = new World();
        var rng = new DeterministicRng(seed);
        for (int i = 0; i < n; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Pos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            w.Set(e, new Vel { X = rng.NextFloat() * 2f - 1f, Y = rng.NextFloat() * 2f - 1f });
        }
        return w;
    }

    private static List<(float X, float Y)> Positions(World w)
    {
        var list = new List<(float, float)>();
        w.ForEach<Pos>((Entity _, ref Pos p) => list.Add((p.X, p.Y)));
        return list;
    }

    private sealed class RecordingScheduler : IJobScheduler
    {
        public int ForCalls { get; private set; }

        public void For(int count, Action<int> body)
        {
            ForCalls++;
            for (int i = 0; i < count; i++) body(i);
        }
    }

    [Fact]
    public void FreshWorld_DefaultScheduler_IsSingleThreaded()
    {
        var w = new World();
        Assert.IsType<SingleThreadedJobScheduler>(w.DefaultScheduler);
    }

    [Fact]
    public void SettingDefaultScheduler_ToNull_Throws()
    {
        var w = new World();
        Assert.Throws<ArgumentNullException>(() => w.DefaultScheduler = null!);
    }

    [Fact]
    public void NoSchedulerArgument_RoutesThroughDefaultScheduler()
    {
        World w = BuildWorld(1, 200);
        var recording = new RecordingScheduler();
        w.DefaultScheduler = recording;

        w.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; }); // no scheduler arg

        Assert.True(recording.ForCalls > 0, "the no-scheduler overload must fan through World.DefaultScheduler");
    }

    [Fact]
    public void BufferedOverload_NoSchedulerArgument_AlsoRoutesThroughDefaultScheduler()
    {
        var w = new World();
        for (int i = 0; i < 50; i++) w.Set(w.Spawn(), new Pos { X = i });
        var recording = new RecordingScheduler();
        w.DefaultScheduler = recording;

        w.ParallelForEach<Pos>((Entity _, ref Pos _, EntityCommandBuffer _) => { }); // no scheduler arg, buffered overload

        Assert.True(recording.ForCalls > 0, "the buffered no-scheduler overload must also fan through World.DefaultScheduler");
    }

    [Fact]
    public void ExplicitScheduler_AlwaysWinsOverDefaultScheduler()
    {
        World w = BuildWorld(2, 200);
        var defaultRecording = new RecordingScheduler();
        var explicitRecording = new RecordingScheduler();
        w.DefaultScheduler = defaultRecording;

        w.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; }, explicitRecording);

        Assert.True(explicitRecording.ForCalls > 0, "the explicitly passed scheduler must run the work");
        Assert.Equal(0, defaultRecording.ForCalls);
    }

    [Fact]
    public void ParallelDefaultScheduler_MatchesForEach_BitIdentical()
    {
        World seq = BuildWorld(42, 5000);
        World par = BuildWorld(42, 5000);
        par.DefaultScheduler = new ThreadPoolJobScheduler();

        seq.ForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; });
        par.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; }); // no scheduler arg

        Assert.Equal(Positions(seq), Positions(par));
    }

    [Fact]
    public void SingleThreadedDefaultScheduler_MatchesForEach_BitIdentical()
    {
        World seq = BuildWorld(7, 1000);
        World par = BuildWorld(7, 1000);
        par.DefaultScheduler = new SingleThreadedJobScheduler(); // explicit, but still the deterministic path

        seq.ForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; });
        par.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; });

        Assert.Equal(Positions(seq), Positions(par));
    }
}
