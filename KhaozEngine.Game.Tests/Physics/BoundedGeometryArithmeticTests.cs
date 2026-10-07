using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Independent rational checks of the finite interval primitives, not a shape or sweep certificate.
public class BoundedGeometryArithmeticTests
{
    [Fact]
    public void DefaultAndNonfiniteInputsAreUnresolved()
    {
        Assert.False(default(GeometryInterval).IsResolved);
        Assert.False(GeometryInterval.Exact(double.NaN).IsResolved);
        Assert.False(GeometryInterval.Exact(double.PositiveInfinity).IsResolved);
        Assert.False(GeometryInterval.Exact(double.NegativeInfinity).IsResolved);
    }

    [Fact]
    public void ReversedOrNonfiniteEndpointsCannotFormAnInterval()
    {
        Assert.False(GeometryInterval.Enclose(2, 1).IsResolved);
        Assert.False(GeometryInterval.Enclose(0, double.PositiveInfinity).IsResolved);
        Assert.False(GeometryInterval.Enclose(double.NaN, 1).IsResolved);
    }

    [Fact]
    public void AdditionEnclosesTheHalfUlpLostByNearestRounding()
    {
        GeometryInterval sum = GeometryInterval.Exact(1).Add(GeometryInterval.Exact(1d / (1L << 53)));
        Contains(sum, (BigInteger.One << 53) + 1, BigInteger.One << 53);
        Assert.True(sum.Lower <= 1 && sum.Upper > 1);
    }

    [Fact]
    public void SubtractionPreservesTheSignOfAnAdjacentFloatDifference()
    {
        GeometryInterval difference = GeometryInterval.Exact(Math.BitIncrement(1d)).Subtract(GeometryInterval.Exact(1));
        Contains(difference, 1, BigInteger.One << 52);
        Assert.True(difference.Lower > 0);
    }

    [Fact]
    public void MultiplicationEnclosesTheExactProductRatherThanItsRoundedValue()
    {
        GeometryInterval value = GeometryInterval.Exact(Math.BitIncrement(1d));
        BigInteger n = (BigInteger.One << 52) + 1;
        Contains(value.Multiply(value), n * n, BigInteger.One << 104);
    }

    [Fact]
    public void DivisionEnclosesANonbinaryRational()
        => Contains(GeometryInterval.Exact(1).Divide(GeometryInterval.Exact(10)), 1, 10);

    [Fact]
    public void AZeroCrossingDenominatorIsUnresolved()
    {
        Assert.False(GeometryInterval.Exact(1).Divide(GeometryInterval.Enclose(-1, 1)).IsResolved);
        Assert.False(GeometryInterval.Exact(0).Divide(GeometryInterval.Exact(0)).IsResolved);
    }

    [Fact]
    public void SquaringAnIntervalAcrossZeroHasANonnegativeLowerBound()
    {
        GeometryInterval square = GeometryInterval.Enclose(-2, 1).Square();
        Contains(square, 0, 1);
        Contains(square, 4, 1);
        Assert.Equal(0d, square.Lower);
    }

    [Fact]
    public void SquareRootBoundsAreCheckedByIndependentSquaredRationals()
    {
        GeometryInterval root = GeometryInterval.Exact(2).Sqrt();
        Assert.True(root.IsResolved && root.Lower >= 0);
        (BigInteger ln, BigInteger ld) = Fraction(root.Lower);
        (BigInteger un, BigInteger ud) = Fraction(root.Upper);
        Assert.True(ln * ln <= 2 * ld * ld);
        Assert.True(un * un >= 2 * ud * ud);
    }

    [Fact]
    public void SubnormalSquareRootHasVerifiedBoundsAndZeroRemainsExact()
    {
        GeometryInterval root = GeometryInterval.Exact(double.Epsilon).Sqrt();
        Contains(root, 1, BigInteger.One << 537);
        GeometryInterval zero = GeometryInterval.Exact(-0d).Sqrt();
        Assert.True(zero.IsResolved);
        Assert.Equal(0d, zero.Lower);
        Assert.Equal(0d, zero.Upper);
    }

    [Fact]
    public void NegativeRootsAndOverflowDoNotPublishFiniteLookingAnswers()
    {
        Assert.False(GeometryInterval.Enclose(-1, 4).Sqrt().IsResolved);
        Assert.False(GeometryInterval.Exact(double.MaxValue).Add(GeometryInterval.Exact(double.MaxValue)).IsResolved);
        Assert.False(GeometryInterval.Exact(double.MaxValue).Multiply(GeometryInterval.Exact(2)).IsResolved);
    }

    [Fact]
    public void UnresolvedOperandsCannotBecomeAnExactZero()
    {
        GeometryInterval unknown = default;
        GeometryInterval zero = GeometryInterval.Exact(0);
        Assert.False(unknown.Add(zero).IsResolved);
        Assert.False(zero.Subtract(unknown).IsResolved);
        Assert.False(unknown.Multiply(zero).IsResolved);
        Assert.False(unknown.Square().IsResolved);
        Assert.False(unknown.Sqrt().IsResolved);
    }

    [Fact]
    public void ExactProductComparisonDetectsCancellationHiddenByBinary64()
    {
        double above = Math.BitIncrement(1d);
        double below = 1d - 1d / (1L << 52);
        Assert.Equal(1d, above * below);
        Assert.Equal(GeometrySign.Negative, BoundedGeometryArithmetic.CompareProducts(above, below, 1, 1));
        Assert.Equal(GeometrySign.Zero, BoundedGeometryArithmetic.CompareProducts(3, 2, 1, 6));
        Assert.Equal(GeometrySign.Positive, BoundedGeometryArithmetic.CompareProducts(-2, -3, 1, 5));
    }

    [Fact]
    public void ExactPredicateRefusesNonfiniteInputsAndItsFixedWorkLimit()
    {
        Assert.Equal(GeometrySign.Unresolved, BoundedGeometryArithmetic.CompareProducts(double.NaN, 1, 0, 1));
        Assert.Equal(GeometrySign.Unresolved, BoundedGeometryArithmetic.CompareProducts(
            double.MaxValue, double.MaxValue, double.Epsilon, double.Epsilon));
    }

    static void Contains(GeometryInterval interval, BigInteger numerator, BigInteger denominator)
    {
        Assert.True(interval.IsResolved);
        (BigInteger ln, BigInteger ld) = Fraction(interval.Lower);
        (BigInteger un, BigInteger ud) = Fraction(interval.Upper);
        Assert.True(ln * denominator <= numerator * ld, "lower bound is above the exact rational");
        Assert.True(un * denominator >= numerator * ud, "upper bound is below the exact rational");
    }

    static (BigInteger Numerator, BigInteger Denominator) Fraction(double value)
    {
        Assert.True(double.IsFinite(value));
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int encoded = (int)((bits >> 52) & 0x7ff);
        BigInteger numerator = bits & 0x000f_ffff_ffff_ffffUL;
        if (encoded != 0) numerator += BigInteger.One << 52;
        int exponent = encoded == 0 ? -1074 : encoded - 1023 - 52;
        if ((bits >> 63) != 0) numerator = -numerator;
        return exponent >= 0 ? (numerator << exponent, BigInteger.One) : (numerator, BigInteger.One << -exponent);
    }
}
