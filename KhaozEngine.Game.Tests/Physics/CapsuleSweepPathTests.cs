using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class CapsuleSweepPathTests
{
    [Fact]
    public void ZeroFractionIsTheExactStartingPoint() =>
        Exact(Point(new Vector3(1f, 2f, 3f), new Vector3(0.25f, -0.5f, 1f), 0), 1, 2, 3);

    [Fact]
    public void ExactlyRepresentableEndpointIsNotNeedlesslyWidened() =>
        Exact(Point(new Vector3(1f, 2f, 3f), new Vector3(0.25f, -0.5f, 1f), 1), 1.25, 1.5, 4);

    [Fact]
    public void ARepresentableInteriorFractionIsTheOriginalPathPoint() =>
        Exact(Point(new Vector3(0f, 1f, -1f), new Vector3(4f, -2f, 8f), 0.25), 1, 0.5, 1);

    [Fact]
    public void NegativeProductsAndSumsKeepTheirSigns() =>
        Exact(Point(new Vector3(-1f, 2f, 0f), new Vector3(-4f, -2f, -2f), 0.5), -3, 1, -1);

    [Fact]
    public void AProductLosingLowBitsRemainsAnEnclosure()
    {
        // 1.1f = 9227469/2^23, the represented 1d/3d = 6004799503160661/2^54.
        BigInteger numerator = new BigInteger(9227469) * 6004799503160661L;
        BigInteger denominator = BigInteger.One << 77;
        double rounded = (double)1.1f * (1d / 3d);
        var r = Fraction(rounded);
        Assert.NotEqual(numerator * r.Denominator, r.Numerator * denominator);
        GeometryVector point = Point(Vector3.Zero, new Vector3(1.1f, 0f, 0f), 1d / 3d);
        Assert.True(point.IsResolved && point.X.Lower < point.X.Upper);
        Contains(point.X, numerator, denominator);
    }

    [Fact]
    public void ACoordinateSumLosingLowBitsCannotBecomeExactOne()
    {
        Assert.Equal(1d, 1d + float.Epsilon);
        GeometryVector point = Point(Vector3.UnitX, new Vector3(float.Epsilon, 0f, 0f), 1);
        Assert.True(point.IsResolved && point.X.Lower < point.X.Upper);
        Contains(point.X, (BigInteger.One << 149) + 1, BigInteger.One << 149);
    }

    [Fact]
    public void ExactAxesStayExactWhenAnotherAxisNeedsAnInterval()
    {
        GeometryVector point = Point(new Vector3(0f, 7f, -3f), new Vector3(1.1f, 0f, 0f), 1d / 3d);
        Assert.True(point.IsResolved && point.X.Lower < point.X.Upper);
        Assert.Equal(7d, point.Y.Lower);
        Assert.Equal(7d, point.Y.Upper);
        Assert.Equal(-3d, point.Z.Lower);
        Assert.Equal(-3d, point.Z.Upper);
    }

    [Fact]
    public void InvalidFractionsCannotPublishAPathPoint()
    {
        foreach (double fraction in new[] { double.NaN, double.PositiveInfinity, -double.Epsilon, Math.BitIncrement(1d) })
            Assert.False(CapsuleSweepPath.Point(Vector3.Zero, Vector3.One, fraction).IsResolved);
    }

    [Fact]
    public void InvalidFullPathCannotBeSalvagedAsAValidPrefixPoint()
    {
        Assert.False(CapsuleSweepPath.Point(new Vector3(float.NaN), Vector3.One, 0).IsResolved);
        Assert.False(CapsuleSweepPath.Point(Vector3.Zero, new Vector3(float.PositiveInfinity), 0).IsResolved);
        Assert.False(CapsuleSweepPath.Point(new Vector3(float.MaxValue), new Vector3(float.MaxValue), 0).IsResolved);
    }

    [Fact]
    public void ReversedParameterizationLocatesTheSameExactPoint()
    {
        Exact(Point(Vector3.Zero, new Vector3(4f, 0f, 0f), 0.25), 1, 0, 0);
        Exact(Point(new Vector3(4f, 0f, 0f), new Vector3(-4f, 0f, 0f), 0.75), 1, 0, 0);
    }

    static void Exact(GeometryVector value, double x, double y, double z)
    {
        Assert.True(value.IsResolved);
        Assert.Equal(x, value.X.Lower);
        Assert.Equal(x, value.X.Upper);
        Assert.Equal(y, value.Y.Lower);
        Assert.Equal(y, value.Y.Upper);
        Assert.Equal(z, value.Z.Lower);
        Assert.Equal(z, value.Z.Upper);
    }

    static void Contains(GeometryInterval interval, BigInteger numerator, BigInteger denominator)
    {
        var lower = Fraction(interval.Lower);
        var upper = Fraction(interval.Upper);
        Assert.True(lower.Numerator * denominator <= numerator * lower.Denominator);
        Assert.True(upper.Numerator * denominator >= numerator * upper.Denominator);
    }

    static (BigInteger Numerator, BigInteger Denominator) Fraction(double value)
    {
        Assert.True(double.IsFinite(value));
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int encoded = (int)((bits >> 52) & 0x7ff);
        BigInteger numerator = bits & 0x000f_ffff_ffff_ffffUL;
        if (encoded != 0) numerator += BigInteger.One << 52;
        if ((bits >> 63) != 0) numerator = -numerator;
        int exponent = encoded == 0 ? -1074 : encoded - 1075;
        return exponent >= 0 ? (numerator << exponent, BigInteger.One) : (numerator, BigInteger.One << -exponent);
    }

    static GeometryVector Point(Vector3 centre, Vector3 displacement, double fraction) =>
        CapsuleSweepPath.Point(centre, displacement, fraction);

}
