using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class XorRngTests
{
    [Fact]
    public void SameSeed_SameStream()
    {
        var a = new XorRng(5);
        var b = new XorRng(5);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void ZeroSeed_DoesNotCollapse()
    {
        var rng = new XorRng(0);
        Assert.NotEqual(0u, rng.NextUInt());
    }

    [Fact]
    public void Copy_IsSnapshot()
    {
        var rng = new XorRng(9);
        rng.NextUInt();
        var snapshot = rng;            // struct copy
        Assert.Equal(snapshot.NextUInt(), rng.NextUInt());
    }

    [Fact]
    public void Range_RespectsBounds()
    {
        var rng = new XorRng(3);
        for (int i = 0; i < 1000; i++) Assert.InRange(rng.Range(2f, 5f), 2f, 5f);
        Assert.Equal(4f, new XorRng(3).Range(4f, 4f));   // degenerate range returns min
    }

    [Fact]
    public void Range_DegenerateStillConsumesExactlyOneDraw()
    {
        // Two identical rngs: one calls the degenerate Range, the other burns one draw by hand.
        // Their streams must line up afterwards, so draw count cannot depend on the bound values.
        var viaRange = new XorRng(77);
        var viaFloat = new XorRng(77);
        Assert.Equal(4f, viaRange.Range(4f, 4f));
        viaFloat.NextFloat();
        for (int i = 0; i < 8; i++)
            Assert.Equal(viaFloat.NextUInt(), viaRange.NextUInt());
    }
}
