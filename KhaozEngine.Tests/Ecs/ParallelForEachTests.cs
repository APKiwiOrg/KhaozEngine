using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

/// <summary>
/// jobs-2 acceptance: data-parallel <see cref="World.ParallelForEach{T1}(RefAction{T1}, IJobScheduler?)"/> must
/// produce world state bit-identical to the sequential <c>ForEach</c> for per-row-pure actions, the debug hazard
/// guard must reject a non-pure action, and deferred (buffered) structural changes must replay deterministically.
/// </summary>
[Collection("AllocSensitive")]  // shares a (serialized) collection with the zero-alloc tests so its heavy
public class ParallelForEachTests   // per-worker allocation never churns the GC during their measurement
{
    private struct Pos : IComponent { public float X, Y; }
    private struct Vel : IComponent { public float X, Y; }
    private struct Flag : IComponent { public int V; }   // splits the population into a second archetype
    private struct Counter : IComponent { public int N; }

    private static World BuildWorld(ulong seed, int n, bool secondArchetype)
    {
        var w = new World();
        var rng = new DeterministicRng(seed);
        for (int i = 0; i < n; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Pos { X = rng.NextFloat() * 100f, Y = rng.NextFloat() * 100f });
            w.Set(e, new Vel { X = rng.NextFloat() * 2f - 1f, Y = rng.NextFloat() * 2f - 1f });
            if (secondArchetype && i % 3 == 0) w.Set(e, new Flag { V = i });
        }
        return w;
    }

    private static List<(float X, float Y)> Positions(World w)
    {
        var list = new List<(float, float)>();
        w.ForEach<Pos>((Entity _, ref Pos p) => list.Add((p.X, p.Y)));
        return list;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelForEach_MatchesForEach_BitIdentical(bool secondArchetype)
    {
        World seq = BuildWorld(42, 5000, secondArchetype);
        World par = BuildWorld(42, 5000, secondArchetype);

        seq.ForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; });
        par.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; },
            new ThreadPoolJobScheduler());

        Assert.Equal(Positions(seq), Positions(par));
    }

    [Fact]
    public void ParallelForEach_DefaultScheduler_IsInline_AndMatchesForEach()
    {
        World seq = BuildWorld(7, 1000, secondArchetype: true);
        World par = BuildWorld(7, 1000, secondArchetype: true);

        seq.ForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; });
        par.ParallelForEach<Pos, Vel>((Entity _, ref Pos p, ref Vel v) => { p.X += v.X; p.Y += v.Y; }); // no scheduler

        Assert.Equal(Positions(seq), Positions(par));
    }

    [Fact]
    public void ParallelForEach_EmptyMatch_IsNoOp()
    {
        var w = new World();
        var ex = Record.Exception(() =>
            w.ParallelForEach<Pos>((Entity _, ref Pos _) => throw new Exception("should not run"),
                new ThreadPoolJobScheduler()));
        Assert.Null(ex);
    }

    [Fact]
    public void HazardGuard_RejectsInlineStructuralChange()
    {
        World w = BuildWorld(1, 100, false);
        Assert.Throws<ParallelAccessViolationException>(() =>
            w.ParallelForEach<Pos>((Entity _, ref Pos _) => w.Spawn()));
    }

    [Fact]
    public void HazardGuard_RejectsWritingAnUndeclaredComponent()
    {
        World w = BuildWorld(1, 100, false);
        Assert.Throws<ParallelAccessViolationException>(() =>
            w.ParallelForEach<Pos>((Entity e, ref Pos _) => w.Set(e, new Counter { N = 1 })));
    }

    [Fact]
    public void HazardGuard_RejectsMutatingAnotherEntityThroughTheWorld()
    {
        World w = BuildWorld(1, 100, false);
        Assert.Throws<ParallelAccessViolationException>(() =>
            w.ParallelForEach<Pos>((Entity e, ref Pos _) => { ref Pos other = ref w.Get<Pos>(e); other.X = 0; }));
    }

    [Fact]
    public void HazardGuard_RejectsReentrantIteration()
    {
        World w = BuildWorld(1, 100, false);
        Assert.Throws<ParallelAccessViolationException>(() =>
            w.ParallelForEach<Pos>((Entity _, ref Pos _) => w.ForEach<Pos>((Entity _, ref Pos _) => { })));
    }

    [Fact]
    public void HazardGuard_CanBeDisabled()
    {
        World on = BuildWorld(1, 10, false);
        Assert.Throws<ParallelAccessViolationException>(() =>
            on.ParallelForEach<Pos>((Entity e, ref Pos _) => on.TryGet<Pos>(e, out _),
                new SingleThreadedJobScheduler()));

        World off = BuildWorld(1, 10, false);
        off.ParallelHazardChecks = false;
        // Inline scheduler ⇒ actually sequential ⇒ this reentrant read is benign; with checks off it must not throw.
        Exception? ex = Record.Exception(() =>
            off.ParallelForEach<Pos>((Entity e, ref Pos _) => off.TryGet<Pos>(e, out _),
                new SingleThreadedJobScheduler()));
        Assert.Null(ex);
    }

    // ---- Buffered (deferred structural change) path ----

    private static List<int> CounterValues(World w)
    {
        var list = new List<int>();
        w.ForEach<Counter>((Entity _, ref Counter c) => list.Add(c.N));
        return list;
    }

    private static World BuildCounterWorld(int n)
    {
        var w = new World();
        for (int i = 0; i < n; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Counter { N = i });
        }
        return w;
    }

    [Fact]
    public void Buffered_DeferredStructuralChanges_MatchSequentialAndAreDeterministic()
    {
        const int n = 2000;

        // Reference: a normal ForEach recording into one ECB (each entity spawns a child carrying 2x its counter).
        World seqRef = BuildCounterWorld(n);
        var ecb = new EntityCommandBuffer();
        seqRef.ForEach<Counter>((Entity _, ref Counter c) =>
        {
            Entity child = ecb.Create();
            ecb.Set(child, new Counter { N = c.N * 2 });
        });
        ecb.Playback(seqRef);
        List<int> reference = CounterValues(seqRef);

        // Parallel buffered, twice on the thread pool + once inline: all must equal the reference, in order.
        List<int> Run(IJobScheduler sched)
        {
            World w = BuildCounterWorld(n);
            w.ParallelForEach<Counter>((Entity _, ref Counter c, EntityCommandBuffer cb) =>
            {
                Entity child = cb.Create();
                cb.Set(child, new Counter { N = c.N * 2 });
            }, sched);
            return CounterValues(w);
        }

        Assert.Equal(reference, Run(new ThreadPoolJobScheduler()));
        Assert.Equal(reference, Run(new ThreadPoolJobScheduler()));   // deterministic run-to-run
        Assert.Equal(reference, Run(new SingleThreadedJobScheduler()));
    }
}
