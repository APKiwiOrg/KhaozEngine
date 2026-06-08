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
}
