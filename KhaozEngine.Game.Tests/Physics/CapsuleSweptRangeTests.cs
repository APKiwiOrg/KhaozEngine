using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Closed subpaths of the original represented request, not rounded replacement paths or accepted movement.
public class CapsuleSweptRangeTests
{
    [Fact]
    public void ClearPrefixDoesNotDeclareTheWholeRequestClear()
    {
        Vector3 delta = new(4f, 0f, 0f);
        GeometryInterval support = GeometryInterval.Exact(-2);
        GeometryInterval whole = CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, 0.5f, delta, -Vector3.UnitX, support);
        Assert.True(whole.IsResolved && whole.Upper < 0);
        Positive(Range(Vector3.Zero, delta, -Vector3.UnitX, support, 0, 0.25), 0.75);
    }

    [Fact]
    public void ClearSuffixDoesNotEraseAnEarlierPossibleIntersection()
    {
        Vector3 delta = new(4f, 0f, 0f);
        GeometryInterval support = GeometryInterval.Exact(1);
        GeometryInterval whole = CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, 0.5f, delta, Vector3.UnitX, support);
        Assert.True(whole.IsResolved && whole.Upper < 0);
        Positive(Range(Vector3.Zero, delta, Vector3.UnitX, support, 0.75, 1), 1.75);
    }

    [Fact]
    public void PositiveTravelProjectionUsesTheBeginningOfTheRange() =>
        Positive(Range(Vector3.Zero, new Vector3(4f, 0f, 0f), Vector3.UnitX,
            GeometryInterval.Exact(0), 0.25, 0.5), 0.75);

    [Fact]
    public void NegativeTravelProjectionUsesTheEndOfTheRange() =>
        Positive(Range(Vector3.Zero, new Vector3(4f, 0f, 0f), -Vector3.UnitX,
            GeometryInterval.Exact(-3), 0.25, 0.5), 0.75);

    [Fact]
    public void ZeroWidthRangeIncludesItsOneClosedPose() =>
        Positive(Range(Vector3.Zero, new Vector3(4f, 0f, 0f), -Vector3.UnitX,
            GeometryInterval.Exact(-3), 0.5, 0.5), 0.75);

    [Fact]
    public void TangencyAtTheRangeEndIsNotDiscarded()
    {
        GeometryInterval value = Range(Vector3.Zero, new Vector3(4f, 0f, 0f), -Vector3.UnitX,
            GeometryInterval.Exact(-1.25), 0, 0.25);
        Contains(value, 0);
        Assert.True(value.Lower <= 0);
    }

    [Fact]
    public void ReversingParameterizationKeepsTheSameSubpath()
    {
        GeometryInterval a = Range(Vector3.Zero, new Vector3(4f, 0f, 0f), Vector3.UnitX,
            GeometryInterval.Exact(0), 0.25, 0.5);
        GeometryInterval b = Range(new Vector3(4f, 0f, 0f), new Vector3(-4f, 0f, 0f), Vector3.UnitX,
            GeometryInterval.Exact(0), 0.5, 0.75);
        Positive(a, 0.75);
        Positive(b, 0.75);
    }

    [Fact]
    public void InvalidFractionsRefuseInsteadOfClampingAUsableRange()
    {
        foreach ((double from, double to) in new[]
        {
            (double.NaN, 0.5), (0d, double.PositiveInfinity), (-double.Epsilon, 0.5),
            (0.5, Math.BitIncrement(1d)), (0.75, 0.5)
        })
            Assert.False(CapsuleSweptSupport.EvaluateRange(Vector3.Zero, 0.25f, 0.5f, Vector3.UnitX, Vector3.UnitY,
                GeometryInterval.Exact(-2), from, to).IsResolved);
    }

    [Fact]
    public void StationaryDisplacementRemainsTheSameCapsuleAcrossANonzeroRange() =>
        Positive(Range(new Vector3(2f, 0f, 0f), Vector3.Zero, Vector3.UnitX,
            GeometryInterval.Exact(0), 0.25, 0.75), 1.75);

    [Fact]
    public void TangentTravelWithZeroDirectionalProjectionRetainsItsMinimum() =>
        Positive(Range(new Vector3(0f, 2f, 0f), new Vector3(4f, 0f, 0f), Vector3.UnitY,
            GeometryInterval.Exact(0), 0.25, 0.75), 1.25);

    static void Positive(GeometryInterval value, double exact)
    {
        Contains(value, exact);
        Assert.True(value.Lower > 0);
    }

    static void Contains(GeometryInterval value, double exact)
    {
        Assert.True(value.IsResolved);
        Assert.True(value.Lower <= exact && value.Upper >= exact);
    }

    static GeometryInterval Range(Vector3 centre, Vector3 displacement, Vector3 axis,
        GeometryInterval support, double from, double to) =>
        CapsuleSweptSupport.EvaluateRange(centre, 0.25f, 0.5f, displacement, axis, support, from, to);

}
