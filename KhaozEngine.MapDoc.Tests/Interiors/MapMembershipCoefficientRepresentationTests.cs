using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipCoefficientRepresentationTests
{
    [Fact]
    public void Coefficients_AreSixteenByteTokensRatherThanRetainedObjects()
    {
        Assert.True(typeof(MapMembershipCoefficient).IsValueType);
        Assert.Equal(16, Unsafe.SizeOf<MapMembershipCoefficient>());
    }

    [Fact]
    public void Affines_AreHandlesToFourContiguousTokens()
    {
        Assert.True(typeof(MapMembershipAffine).IsValueType);
        Assert.Equal(16, Unsafe.SizeOf<MapMembershipAffine>());
    }

    public static IEnumerable<object[]> Values()
    {
        BigInteger[] values = [0, long.MinValue, long.MaxValue, (BigInteger)long.MinValue - 1,
            (BigInteger)long.MaxValue + 1, (BigInteger.One << 53) - 1, (BigInteger.One << 53) + 1,
            (BigInteger.One << 1924) + 7];
        foreach (var value in values)
        {
            yield return [value, -value + 1, new BigInteger(3), BigInteger.One];
            yield return [-6 * value, 6 * value - 6, new BigInteger(-18), new BigInteger(-6)];
        }
        yield return [BigInteger.Zero, BigInteger.Zero, (BigInteger)long.MinValue, BigInteger.One];
        yield return [BigInteger.Zero, BigInteger.Zero, BigInteger.One << 100, BigInteger.One << 101];
        yield return [BigInteger.One << 100, BigInteger.Zero, BigInteger.One, BigInteger.One << 99];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void NormalizedEvaluation_AgreesWithIndependentBigIntegerRatios(BigInteger x, BigInteger z,
        BigInteger c, BigInteger d)
    {
        var affine = new MapMembershipAffine(x, z, c, d);
        var exact = new MapMembershipExact();
        MapExactXz[] points = [new(default, default), new(new(1, 3), new(1, 3)),
            new(new(9007199254740993, 9007199254740992), new(1, 1)), new(new(-7, 11), new(13, 17))];
        foreach (var p in points)
        {
            BigInteger numerator = x * p.X.Numerator * p.Z.Denominator + z * p.Z.Numerator * p.X.Denominator +
                c * p.X.Denominator * p.Z.Denominator;
            BigInteger denominator = d * p.X.Denominator * p.Z.Denominator;
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            BigInteger gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
            numerator /= gcd; denominator /= gcd;
            Assert.Equal(numerator.Sign, exact.Sign(affine, p));
            if (affine.Filter(p) is { } filtered) Assert.Equal(numerator.Sign, filtered);
            bool representable = numerator >= long.MinValue && numerator <= long.MaxValue && denominator <= long.MaxValue;
            Assert.Equal(representable, exact.TryValue(affine, p, out var value));
            if (representable) Assert.Equal(new MapExactValue((long)numerator, (long)denominator), value);
            double expected = numerator.IsZero ? 0 : (double)numerator / (double)denominator;
            Assert.Equal(expected, exact.ValueDouble(affine, p));
        }
    }

    [Fact]
    public void Ceiling_IsAppliedAfterNormalizationAndStillRefusesOneBitBeyond1925()
    {
        _ = new MapMembershipAffine(BigInteger.One << 1924, 0, 1, 1);
        Assert.Throws<MapExactOverflowException>(() => new MapMembershipAffine(BigInteger.One << 1925, 0, 1, 1));
        var reduced = new MapMembershipAffine(BigInteger.One << 1925, 0, BigInteger.One << 1925, BigInteger.One << 1925);
        Assert.True(new MapMembershipExact().TryValue(reduced, new(new(1, 1), default), out var value));
        Assert.Equal(new MapExactValue(2, 1), value);
    }
}
