using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class CapsuleSweepDistanceEncodingTests
{

    [Fact]
    public void AxisBracketKeepsBothExactDistanceBounds()
    {
        CapsuleSweepResult result = Hit(new(4, 0, 0), 0.3125 - 1d / 4096, 0.3125);
        Accepted(result);
        Assert.Equal(1.2490234375f, result.ClearThroughDistance);
        Assert.Equal(1.25f, result.ImpactDistance);
    }

    [Fact]
    public void PythagoreanNormCanTightenToAnExactValue()
    {
        CapsuleSweepResult result = Hit(new(3, 4, 0), 0.25, 0.25 + 1d / 8192);
        Accepted(result);
        Assert.Equal(1.25f, result.ClearThroughDistance);
        Assert.Equal(1.2506103515625f, result.ImpactDistance);
    }

    [Fact]
    public void IrrationalNormBoundsAreRoundedOutward()
    {
        double from = 0.25, to = 0.25 + 1d / 8192;
        CapsuleSweepResult result = Hit(new(1, 2, 0), from, to);
        Accepted(result);
        // Products of these binary32 bounds and dyadic fractions are exact in binary64.
        double lower = result.ClearThroughDistance, upper = result.ImpactDistance!.Value;
        Assert.True(lower * lower <= 5d * from * from);
        Assert.True(upper * upper >= 5d * to * to);
        Assert.True(upper * upper <= 5d);
    }

    [Fact]
    public void ExactlyRepresentableEndpointRetainsItsHit()
    {
        CapsuleSweepResult result = Hit(new(4, 0, 0), 1, 1, 0);
        Accepted(result, 0);
        Assert.Equal(4f, result.ClearThroughDistance);
        Assert.Equal(4f, result.ImpactDistance);
    }

    [Fact]
    public void IrrationalEndpointRefusesInsteadOfClampingAwayTheWitness()
    {
        double roundedUp = (float)Math.Sqrt(5d);
        Assert.True(roundedUp * roundedUp > 5d);
        Refused(Hit(new(1, 2, 0), 1, 1));
    }

    [Fact]
    public void InitialContactRemainsZeroEvenWithIrrationalNorm()
    {
        CapsuleSweepResult result = Hit(new(1, 2, 0), 0, 0, 0);
        Accepted(result, 0);
        Assert.Equal(0f, result.ClearThroughDistance);
        Assert.Equal(0f, result.ImpactDistance);
    }

    [Fact]
    public void ZeroDisplacementHasZeroDistance()
    {
        CapsuleSweepResult result = Hit(Vector3.Zero, 0, 1, 0);
        Accepted(result, 0);
        Assert.Equal(0f, result.ClearThroughDistance);
        Assert.Equal(0f, result.ImpactDistance);
    }

    [Fact]
    public void SubnormalDistanceDoesNotRoundBothBoundsToZero()
    {
        CapsuleSweepResult result = Hit(new(float.Epsilon, 0, 0), 0.5, 0.5);
        Accepted(result);
        Assert.Equal(0f, result.ClearThroughDistance);
        Assert.Equal(float.Epsilon, result.ImpactDistance);
        Assert.True(result.CertifiedErrorMetres >= float.Epsilon);
    }

    [Fact]
    public void WideBracketsRefuseWithoutAPrefix() =>
        Refused(Hit(new(4, 0, 0), 0.25, 0.5));

    [Fact]
    public void MalformedFractionsAndErrorLimitsRefuse()
    {
        foreach ((double from, double to) in new[]
        {
            (-double.Epsilon, 0.5), (0d, Math.BitIncrement(1d)), (0.5, 0.25),
            (double.NaN, 0.5), (0d, double.PositiveInfinity)
        }) Refused(Hit(Vector3.UnitX, from, to));
        Refused(Hit(Vector3.UnitX, 0.5, 0.5, -float.Epsilon));
        Refused(Hit(Vector3.UnitX, 0.5, 0.5, float.NaN));
    }

    [Fact]
    public void NonfiniteAndUnencodableDistancesRefuse()
    {
        Refused(Hit(new(float.NaN, 0, 0), 0, 1));
        Refused(Hit(new(float.MaxValue, float.MaxValue, 0), 0, 1));
    }

    [Fact]
    public void ZeroErrorRequiresAnExactlyEncodablePoint()
    {
        Accepted(Hit(new(3, 4, 0), 0.25, 0.25, 0), 0);
        Refused(Hit(new(1, 2, 0), 0.25, 0.25, 0));
    }

    static void Accepted(CapsuleSweepResult result, float limit = 0.001f)
    {
        Assert.True(result.IsValid);
        Assert.Equal(CapsuleSweepStatus.Hit, result.Status);
        Assert.NotNull(result.ImpactDistance);
        double width = (double)result.ImpactDistance.Value - result.ClearThroughDistance;
        Assert.InRange(width, 0d, result.CertifiedErrorMetres);
        Assert.InRange(result.CertifiedErrorMetres, 0f, limit);
    }

    static void Refused(CapsuleSweepResult result)
    {
        Assert.Equal(default, result);
        Assert.Equal(CapsuleSweepStatus.Unresolved, result.Status);
    }

    static CapsuleSweepResult Hit(Vector3 delta, double from, double to, float error = 0.001f) =>
        CapsuleSweepDistanceEncoding.Hit(delta, from, to, error);
}
