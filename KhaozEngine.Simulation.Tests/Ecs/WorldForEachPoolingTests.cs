using System;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

file struct Pos : IComponent { public float X; }

/// <summary>
/// The parameterless <see cref="World.ForEach{T1}(RefAction{T1})"/> overloads used to allocate a fresh
/// <see cref="Query"/> (and its three backing lists) on every call. They now rent a recycled Query from
/// an internal per-World pool, so a steady-state ForEach loop allocates nothing. These tests pin that:
/// the GC-allocation reuse assertion proves zero per-call allocation, and the pool/nesting tests prove
/// the recycling is correct and aliasing-free.
/// </summary>
[Collection("AllocSensitive")]  // its zero-alloc assertion must not run alongside the GC-churning parallel tests
public class WorldForEachPoolingTests
{
    private static World MakeWorld(int entities)
    {
        var w = new World();
        for (int i = 0; i < entities; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Pos { X = 1f });
        }
        return w;
    }

    [Fact]
    public void ForEachDoesNotAllocatePerCallInSteadyState()
    {
        var w = MakeWorld(16);

        float total = 0f;
        // Hoist the delegate out of the measured loop so the only allocation under test is the Query itself.
        RefAction<Pos> action = (Entity _, ref Pos p) => total += p.X;

        // Warm up: JIT + first Refresh build the matched-archetype list (one-time, grows backing arrays).
        for (int i = 0; i < 200; i++) w.ForEach(action);

        total = 0f;
        const int iterations = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) w.ForEach(action);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(16 * iterations, total);   // sanity: the loop actually iterated every entity
        long allocated = after - before;
        // A fresh Query + 3 Lists is >=150 bytes; un-pooled this loop would allocate >300 KB. Pooled it is
        // ~0; allow a tiny margin for incidental runtime noise (well under one Query's cost per iteration).
        Assert.True(allocated < 8192, $"ForEach allocated {allocated} bytes over {iterations} calls (expected ~0)");
    }

    [Fact]
    public void RentedQueryIsReturnedToPoolAfterForEach()
    {
        var w = MakeWorld(4);

        // Pool is prewarmed and idle before any ForEach.
        Assert.Equal(0, w._forEachQueryPool.ActiveCount);
        int free = w._forEachQueryPool.FreeCount;
        Assert.True(free >= 1);

        w.ForEach((Entity _, ref Pos _) => { });

        // The rented query is back; no leak.
        Assert.Equal(0, w._forEachQueryPool.ActiveCount);
        Assert.Equal(free, w._forEachQueryPool.FreeCount);
    }

    [Fact]
    public void NestedForEachProducesCorrectResultsWithoutAliasing()
    {
        var w = MakeWorld(3);   // three entities, X = 1 each

        // Outer iterates entities; inner runs a full ForEach inside the action. If the outer and inner
        // shared one pooled Query the outer's matched set would be clobbered mid-iteration. With distinct
        // instances per nesting level, the outer still visits all 3 entities and the inner sees all 3 each.
        int outerVisits = 0;
        float innerSumAcrossOuter = 0f;
        w.ForEach((Entity _, ref Pos _) =>
        {
            outerVisits++;
            float innerSum = 0f;
            w.ForEach((Entity _, ref Pos ip) => innerSum += ip.X);
            innerSumAcrossOuter += innerSum;
        });

        Assert.Equal(3, outerVisits);              // outer iteration not truncated by the nested call
        Assert.Equal(9f, innerSumAcrossOuter);     // inner saw all 3 entities on each of the 3 outer steps
    }
}
