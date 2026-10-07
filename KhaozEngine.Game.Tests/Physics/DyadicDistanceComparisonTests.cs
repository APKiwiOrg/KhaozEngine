using System;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Exact signs for supplied represented coordinates. No earlier transform or geometry premise is inferred.
public class DyadicDistanceComparisonTests
{
    [Fact]
    public void ClosedPythagoreanTangencyHasExactlyZeroSign() =>
        Assert.Equal(GeometrySign.Zero, Sign([0.375, 0.5], [0, 0], [0.625], [0]));

    [Fact]
    public void InsideAndOutsideRemainDistinct()
    {
        Assert.Equal(GeometrySign.Negative, Sign([0.5], [0], [0.625], [0]));
        Assert.Equal(GeometrySign.Positive, Sign([0.75], [0], [0.625], [0]));
    }

    [Fact]
    public void APositiveTermLostByDoubleSummationIsStillPositive()
    {
        double tiny = 1d / (1L << 27);
        Assert.Equal(1d, 1d + tiny * tiny);
        Assert.Equal(GeometrySign.Positive, Sign([1, tiny], [0, 0], [1], [0]));
    }

    [Fact]
    public void SubtractionOccursBeforeAnyLossOfTheDyadicDifference()
    {
        Assert.Equal(1d, 1d - double.Epsilon);
        Assert.Equal(GeometrySign.Negative, Sign([1], [double.Epsilon], [1], [0]));
    }

    [Fact]
    public void TranslationAndPairDirectionDoNotChangeTheSquaredDistance()
    {
        Assert.Equal(GeometrySign.Zero, Sign([54.375, 1.5], [54, 1], [0.625], [0]));
        Assert.Equal(GeometrySign.Zero, Sign([54, 1], [54.375, 1.5], [0.625], [0]));
    }

    [Fact]
    public void SwappingDistancesReversesTheirStrictOrder()
    {
        Assert.Equal(GeometrySign.Positive, Sign([3, 4, 1], [0, 0, 0], [5], [0]));
        Assert.Equal(GeometrySign.Negative, Sign([5], [0], [3, 4, 1], [0, 0, 0]));
    }

    [Fact]
    public void InvalidDimensionsAndNonfiniteCoordinatesAreUnresolved()
    {
        Assert.Equal(GeometrySign.Unresolved, Sign([], [], [1], [0]));
        Assert.Equal(GeometrySign.Unresolved, Sign([1, 2], [0], [1], [0]));
        Assert.Equal(GeometrySign.Unresolved, Sign([1, 2, 3, 4], [0, 0, 0, 0], [1], [0]));
        Assert.Equal(GeometrySign.Unresolved, Sign([double.NaN], [0], [1], [0]));
        Assert.Equal(GeometrySign.Unresolved, Sign([1], [0], [double.PositiveInfinity], [0]));
    }

    [Fact]
    public void OversizedExactIntermediateRefusesBeforeSquaring()
        => Assert.Equal(GeometrySign.Unresolved, Sign([double.MaxValue], [double.Epsilon], [0], [0]));

    static GeometrySign Sign(double[] a, double[] b, double[] c, double[] d) =>
        BoundedGeometryArithmetic.CompareSquaredDistances(a, b, c, d);
}
