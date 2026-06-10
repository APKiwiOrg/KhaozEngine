using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class DeterministicRngTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new DeterministicRng(1337);
        var b = new DeterministicRng(1337);
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
        Assert.True(sa.Distinct().Count() > 8);             // not a constant stream
    }

    [Fact]
    public void KnownVectorLocksAlgorithm()
    {
        // Captured from the implementation (xorshift128+ seeded via splitmix64, seed 42).
        var r = new DeterministicRng(42);
        ulong[] expected = { 12706997879443677767, 13388708669165669496, 16395596082725179435 };
        Assert.Equal(expected, new[] { r.NextULong(), r.NextULong(), r.NextULong() });
    }

    [Fact]
    public void StateRoundTrips()
    {
        var r = new DeterministicRng(99);
        r.NextULong(); r.NextULong();
        var saved = r.State;
        ulong next = r.NextULong();
        var restored = new DeterministicRng(1) { State = saved };
        Assert.Equal(next, restored.NextULong());          // resumes the exact sequence
    }

    [Fact]
    public void RangeAndFloatBounds()
    {
        var r = new DeterministicRng(7);
        for (int i = 0; i < 1000; i++)
        {
            int n = r.Next(10, 20);
            Assert.InRange(n, 10, 19);
            float f = r.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void CreateDerived_SameNameSameStream()
    {
        var a = new DeterministicRng(2024).CreateDerived("combat");
        var b = new DeterministicRng(2024).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
    }

    [Fact]
    public void CreateDerived_DifferentNamesDifferentStreams()
    {
        var parent = new DeterministicRng(2024);
        var combat = parent.CreateDerived("combat");
        var ore = parent.CreateDerived("oreField");
        var sc = Enumerable.Range(0, 16).Select(_ => combat.NextULong()).ToArray();
        var so = Enumerable.Range(0, 16).Select(_ => ore.NextULong()).ToArray();
        Assert.NotEqual(sc, so);
    }

    [Fact]
    public void CreateDerived_DifferentParentSeedsDifferentStreams()
    {
        var a = new DeterministicRng(1).CreateDerived("combat");
        var b = new DeterministicRng(2).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.NotEqual(sa, sb);
    }

    [Fact]
    public void CreateDerived_StreamsAreIndependent()
    {
        // Drain one named stream from one parent; an untouched parent's same-named
        // stream must be unaffected, and a sibling name must match across both parents.
        var p1 = new DeterministicRng(42);
        var combat1 = p1.CreateDerived("combat");
        var ore1 = p1.CreateDerived("oreField");
        for (int i = 0; i < 50; i++) combat1.NextULong();

        var p2 = new DeterministicRng(42);
        var ore2 = p2.CreateDerived("oreField");

        var s1 = Enumerable.Range(0, 32).Select(_ => ore1.NextULong()).ToArray();
        var s2 = Enumerable.Range(0, 32).Select(_ => ore2.NextULong()).ToArray();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void CreateDerived_IsOrderIndependent()
    {
        // Derivation comes from the construction seed, not live draw state:
        // draining the parent first must not change the derived stream.
        var early = new DeterministicRng(7).CreateDerived("combat");

        var parent = new DeterministicRng(7);
        for (int i = 0; i < 100; i++) parent.NextULong();
        var late = parent.CreateDerived("combat");

        var se = Enumerable.Range(0, 16).Select(_ => early.NextULong()).ToArray();
        var sl = Enumerable.Range(0, 16).Select(_ => late.NextULong()).ToArray();
        Assert.Equal(se, sl);
    }

    [Fact]
    public void CreateDerived_NullNameThrows()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new DeterministicRng(1).CreateDerived(null!));
    }
}
