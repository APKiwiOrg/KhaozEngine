using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class DeterministicRngTests
{
    [Fact]
    public void SameSeed_SameStream()
    {
        var a = new DeterministicRng(1234);
        var b = new DeterministicRng(1234);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void State_SaveRestore_ReproducesStream()
    {
        var rng = new DeterministicRng(99);
        rng.NextULong(); rng.NextULong();
        var saved = rng.State;
        ulong[] expected = { rng.NextULong(), rng.NextULong(), rng.NextULong() };
        rng.State = saved;
        Assert.Equal(expected, new[] { rng.NextULong(), rng.NextULong(), rng.NextULong() });
    }

    [Fact]
    public void NextFloat_InUnitInterval()
    {
        var rng = new DeterministicRng(7);
        for (int i = 0; i < 1000; i++)
        {
            float f = rng.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void StableHash_IsPublicAndDeterministic()
    {
        Assert.Equal(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("combat"));
        Assert.NotEqual(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("oreField"));
    }

    [Fact]
    public void CreateDerived_IsIndependentOfDrawState()
    {
        var parent = new DeterministicRng(42);
        var d1 = parent.CreateDerived("combat");
        parent.NextULong(); parent.NextULong();
        var d2 = parent.CreateDerived("combat");
        Assert.Equal(d1.NextULong(), d2.NextULong());
    }
}
