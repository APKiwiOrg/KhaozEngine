using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

file struct Counter : IComponent { public int N; }

/// <summary>
/// Pins the allocation and pooling behaviour added when ParallelForEach stopped allocating a closure per matched
/// archetype and started pooling its EntityCommandBuffers:
/// <list type="bullet">
/// <item>the non-buffered and buffered parallel passes allocate nothing per call in steady state (the per-arity
/// chunk context and its Body delegate are cached on the pooled Query, the buffers and sink list are pooled),</item>
/// <item>the World EntityCommandBuffer pool reuses buffers across buffered calls,</item>
/// <item>the public caller-supplied-sink Query overload is pool-neutral (fresh caller-owned buffers, so
/// external use cannot drain the World pool),</item>
/// <item>a buffered pass whose action throws drops its dirty buffers instead of returning them to the pool, and</item>
/// <item>repeated buffered passes that reuse the pool stay bit-for-bit equal to the sequential reference.</item>
/// </list>
/// </summary>
[Collection("AllocSensitive")]  // its zero-alloc assertions must not run alongside the GC-churning parallel tests
public class ParallelForEachPoolingTests
{
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

    private static int CountEntities(World w)
    {
        int c = 0;
        w.ForEach<Counter>((Entity _, ref Counter _) => c++);
        return c;
    }

    private static List<int> SortedCounters(World w)
    {
        var list = new List<int>();
        w.ForEach<Counter>((Entity _, ref Counter c) => list.Add(c.N));
        list.Sort();
        return list;
    }

    [Fact]
    public void NonBufferedParallelForEach_DoesNotAllocatePerCallInSteadyState()
    {
        var w = BuildCounterWorld(64);   // single archetype
        var sched = new SingleThreadedJobScheduler();   // inline: deterministic and free of thread-pool alloc noise

        long sum = 0;
        // Hoist the delegate (its capture of `sum` allocates once) out of the measured loop.
        RefAction<Counter> action = (Entity _, ref Counter c) => sum += c.N;

        for (int i = 0; i < 200; i++) w.ParallelForEach(action, sched);   // warm JIT, context cache, query pool

        sum = 0;
        const int iterations = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) w.ParallelForEach(action, sched);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(iterations * (64L * 63L / 2L), sum);   // sanity: every entity was visited each call
        long allocated = after - before;
        // Un-pooled this allocated a closure + Action<int> per archetype AND a per-call section closure - hundreds
        // of KB over this loop. With the cached context and inlined section bracket it is ~0.
        Assert.True(allocated < 8192, $"non-buffered ParallelForEach allocated {allocated} bytes over {iterations} calls (expected ~0)");
    }

    [Fact]
    public void BufferedParallelForEach_DoesNotAllocatePerCallInSteadyState()
    {
        var w = BuildCounterWorld(64);
        var sched = new SingleThreadedJobScheduler();
        // Empty action: still rents k buffers per archetype (the allocation under test) but records nothing, so
        // playback is a no-op and the buffers return to the pool clean.
        RefBufferAction<Counter> action = (Entity _, ref Counter _, EntityCommandBuffer _) => { };

        for (int i = 0; i < 200; i++) w.ParallelForEach(action, sched);   // warm the buffer + sink pools to capacity

        const int iterations = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) w.ParallelForEach(action, sched);
        long after = GC.GetAllocatedBytesForCurrentThread();

        long allocated = after - before;
        // Budget: the chunk context + its Body delegate, the k command buffers, the sink list and the section
        // bracket are all reused across calls, so a steady-state buffered pass allocates nothing. The old path
        // allocated a fresh EntityCommandBuffer[k] + k buffers + a sink List + closures every call.
        Assert.True(allocated < 8192, $"buffered ParallelForEach allocated {allocated} bytes over {iterations} calls (expected ~0)");
    }

    [Fact]
    public void BufferedParallelForEach_ReusesPooledBuffersAcrossCalls()
    {
        var w = BuildCounterWorld(64);
        var sched = new SingleThreadedJobScheduler();
        RefBufferAction<Counter> noop = (Entity _, ref Counter _, EntityCommandBuffer _) => { };

        w.ParallelForEach(noop, sched);
        int afterFirst = w._ecbPool.Count;
        Assert.True(afterFirst > 0, "buffers must be returned to the pool after playback");

        w.ParallelForEach(noop, sched);
        Assert.Equal(afterFirst, w._ecbPool.Count);   // an identical pass rented every buffer from the pool, allocated none
    }

    [Fact]
    public void PublicSinkOverload_IsPoolNeutral()
    {
        var w = BuildCounterWorld(64);
        var sched = new SingleThreadedJobScheduler();

        // Warm the World pool via the World overload so there are pooled buffers to (wrongly) drain.
        w.ParallelForEach<Counter>((Entity _, ref Counter _, EntityCommandBuffer _) => { }, sched);
        int pooled = w._ecbPool.Count;
        Assert.True(pooled > 0);

        // The public caller-supplied-sink overload hands out fresh caller-owned buffers: an external caller never
        // returns them, so drawing them from the pool would silently drain it. The pool count must not move.
        var sink = new List<EntityCommandBuffer>();
        w.Query().ParallelForEach<Counter>((Entity _, ref Counter _, EntityCommandBuffer _) => { }, sched, sink);
        Assert.True(sink.Count > 0);
        Assert.Equal(pooled, w._ecbPool.Count);
    }

    [Fact]
    public void BufferedParallelForEach_DropsDirtyBuffersOnException()
    {
        var w = BuildCounterWorld(64);
        var sched = new SingleThreadedJobScheduler();

        // Warm the pool with a clean pass so there are pooled buffers a bug could wrongly hand back out dirty.
        w.ParallelForEach<Counter>((Entity _, ref Counter _, EntityCommandBuffer _) => { }, sched);
        int pooledBefore = w._ecbPool.Count;
        Assert.True(pooledBefore > 0);

        // A pass whose action records a command then throws: the rented buffers are dirty and must be dropped.
        Assert.Throws<InvalidOperationException>(() =>
            w.ParallelForEach<Counter>((Entity _, ref Counter _, EntityCommandBuffer cb) =>
            {
                cb.Create();
                throw new InvalidOperationException("boom");
            }, sched));

        // The dirty buffers were consumed from the pool and dropped, so the count did not grow.
        Assert.True(w._ecbPool.Count <= pooledBefore,
            $"pool grew from {pooledBefore} to {w._ecbPool.Count}: a dirty buffer was returned");

        // Strongest proof: a following clean pass produces exactly one child per entity. A reused dirty buffer
        // still holding the "boom" Create would spawn phantom entities and break this count.
        int before = CountEntities(w);
        w.ParallelForEach<Counter>((Entity _, ref Counter c, EntityCommandBuffer cb) =>
        {
            Entity child = cb.Create();
            cb.Set(child, new Counter { N = c.N });
        }, sched);
        Assert.Equal(before * 2, CountEntities(w));
    }

    [Fact]
    public void BufferedParallelForEach_RepeatedPoolReuse_MatchesSequential()
    {
        static void ParallelPass(World w, IJobScheduler s) =>
            w.ParallelForEach<Counter>((Entity _, ref Counter c, EntityCommandBuffer cb) =>
            {
                Entity child = cb.Create();
                cb.Set(child, new Counter { N = c.N + 1 });
            }, s);

        static void SequentialPass(World w)
        {
            var ecb = new EntityCommandBuffer();
            w.ForEach<Counter>((Entity _, ref Counter c) =>
            {
                Entity child = ecb.Create();
                ecb.Set(child, new Counter { N = c.N + 1 });
            });
            ecb.Playback(w);
        }

        World par = BuildCounterWorld(300);
        World seq = BuildCounterWorld(300);
        var sched = new ThreadPoolJobScheduler();

        // Three passes exercise pool reuse (each pass rents, plays back, returns the buffers) under real concurrency.
        // The transformation is a pure function of the counter multiset, so order-independent equality is exact.
        for (int pass = 0; pass < 3; pass++) { ParallelPass(par, sched); SequentialPass(seq); }

        Assert.Equal(SortedCounters(seq), SortedCounters(par));
    }
}
