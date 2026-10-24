using Xunit;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexPlanarPenetrationTests
{
    [Fact]
    public void ExactPointEqualityUsesValuesInsteadOfStoredZeroDenominators()
    {
        P zero = default, constructed = new(Q.From(0), Q.From(0), Q.From(0));
        Assert.Equal(zero, constructed);
        Assert.True(zero == constructed);
        Assert.False(zero != constructed);
        Assert.Equal(zero.GetHashCode(), constructed.GetHashCode());
        Assert.NotEqual(zero, new P(Q.From(0.125), default, default));
        Assert.NotEqual(zero, new P(default, Q.From(-0.125), default));
        Assert.NotEqual(zero, new P(default, default, Q.From(0.125)));
        P radical = new(Q.Root(Q.From(2).A), default, default);
        P equivalent = new(Q.Root(Q.From(8).A) * Q.From(0.5), Q.From(0), Q.From(0));
        Assert.Equal(radical, equivalent);
        Assert.Equal(radical.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(radical, new P(Q.Root(Q.From(3).A), default, default));
    }
}
