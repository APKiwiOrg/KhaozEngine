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

    /// <summary>
    /// One level deeper than the pool is prewarmed for, which is the branch the earlier nesting test never
    /// reaches. The pool holds 4 instances, so levels 1 to 4 each rent one and level 5 finds it empty:
    /// <c>TryRent</c> comes back false, writes the empty rental, and <c>RentForEachQuery</c> falls back to a
    /// fresh un-pooled Query. That level still iterates correctly, and its <c>finally</c> hands the empty
    /// rental to <c>TryReturn</c>, which has to ABSORB it rather than throw, because a throw there would
    /// replace whatever exception the body was already unwinding. The pool ends the whole descent back at 4
    /// free, so the fallback level leaks nothing into the pool it never took anything from.
    /// </summary>
    [Fact]
    public void ForEachNestedPastThePrewarmFallsBackAndTheEmptyRentalIsAbsorbed()
    {
        var w = MakeWorld(3);   // three entities, X = 1 each

        Assert.Equal(4, w._forEachQueryPool.FreeCount);   // the prewarm, and so the depth the pool covers
        Assert.Equal(0, w._forEachQueryPool.ActiveCount);

        var visitsAtDepth = new int[5];
        float deepestSum = 0f;
        int exhaustionChecks = 0;

        void Descend(int depth)
        {
            bool descendedFromHere = false;
            w.ForEach((Entity _, ref Pos p) =>
            {
                visitsAtDepth[depth - 1]++;

                if (depth < 5)
                {
                    // Descend once per level, so the deepest level runs a single time rather than 3^4 times.
                    if (descendedFromHere) return;
                    descendedFromHere = true;
                    Descend(depth + 1);
                    return;
                }

                deepestSum += p.X;
                exhaustionChecks++;

                // Every pooled instance is out at levels 1 to 4, so this level is running on the fallback.
                Assert.Equal(4, w._forEachQueryPool.ActiveCount);
                Assert.Equal(0, w._forEachQueryPool.FreeCount);

                // The exhausted path itself, asserted directly rather than inferred from the result.
                Assert.False(w._forEachQueryPool.TryRent(out var empty));
                Assert.True(empty.IsEmpty);
                Assert.Null(empty.Item);
                Assert.False(w._forEachQueryPool.TryReturn(in empty));   // absorbed, not thrown
            });
        }

        Descend(1);

        Assert.Equal(new[] { 3, 3, 3, 3, 3 }, visitsAtDepth);   // no level truncated by the one below it
        Assert.Equal(3f, deepestSum);                           // the fallback level saw all 3 entities
        Assert.Equal(3, exhaustionChecks);                      // and stayed exhausted for all 3 of them
        Assert.Equal(0, w._forEachQueryPool.ActiveCount);
        Assert.Equal(4, w._forEachQueryPool.FreeCount);          // every pooled rental came back
    }
}
