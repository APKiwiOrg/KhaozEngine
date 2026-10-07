using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementCapsuleRoundingTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    delegate bool EncloseDelegate(in MovementBodyQuery body, Vector3 delta, out MovementBodyQuery enclosed,
        out Vector3 endpoint, out double inflation);
    delegate bool ImpactDelegate(Vector3 centre, Vector3 delta, double distance, double length,
        out Vector3 point, out double error);

    [Fact]
    public void PositiveLostLowBitsRemainInsideTheQueriedCapsule() =>
        Check(new(1, 0.75f, 0), new(MathF.ScaleB(1, -54), 0, 0));

    [Fact]
    public void NegativeLostLowBitsRemainInsideTheQueriedCapsule() =>
        Check(new(-1, 0.75f, 0), new(-MathF.ScaleB(1, -54), 0, 0));

    [Fact]
    public void AllThreeCoordinateRoundingErrorsAreCoveredTogether() =>
        Check(new(1, 2, -3), new(MathF.ScaleB(1, -25), -MathF.ScaleB(1, -25), MathF.ScaleB(1, -25)));

    [Fact]
    public void RepresentedCylinderConversionCannotShrinkTheRequestedShape() =>
        Check(new(0, 0.75f, 0), new(0.1f, 0.2f, 0.3f), radius: 0.3f);

    [Fact]
    public void ACoarseCoordinateWithSmallEndpointErrorStillHasABoundedEnclosure() =>
        Check(new(40000, 0.75f, 0), new(0.0001f, 0, 0));

    [Fact]
    public void AnEndpointErrorBeyondTheSkinRefusesTheQueryShape()
    {
        var body = Body(new(65536, 0.75f, 0));
        Assert.False(Enclose()(body, new(0.003f, 0, 0), out MovementBodyQuery enclosure, out _, out _));
        Assert.Equal(default, enclosure);
    }

    [Fact]
    public void StationaryEnclosureContainsTheRequestedCapsule() => Check(new(0, 0.75f, 0), Vector3.Zero);

    [Fact]
    public void AxisImpactUsesTheOriginalCapsuleCentre()
    {
        Assert.True(Impact()(new(1, 2, 3), new(4, 0, 0), 1, 4, out Vector3 point, out double error));
        Assert.Equal(new Vector3(2, 2, 3), point);
        Assert.InRange(error, 0d, 0.001d);
    }

    [Fact]
    public void PythagoreanImpactContainsTheIndependentRationalPoint()
    {
        Assert.True(Impact()(Vector3.Zero, new(3, 4, 0), 1, 5, out Vector3 point, out double error));
        // Exact point is (3/5,4/5,0). Compare integer dyadics, not another floating norm.
        BigInteger unit = BigInteger.One << 1074;
        BigInteger scaledError = BigInteger.Abs(5 * Units(point.X) - 3 * unit) +
            BigInteger.Abs(5 * Units(point.Y) - 4 * unit) + 5 * BigInteger.Abs(Units(point.Z));
        Assert.True(scaledError <= 5 * Units(error));
    }

    [Fact]
    public void InvalidAndUncertifiableImpactPosesRefuse()
    {
        Assert.False(Impact()(Vector3.Zero, Vector3.UnitX, 2, 1, out _, out _));
        Assert.False(Impact()(Vector3.Zero, Vector3.UnitX, double.NaN, 1, out _, out _));
        Assert.False(Impact()(new(50000, 0, 0), new(4, 0, 0), 1, 4, out _, out _));
    }

    static void Check(Vector3 centre, Vector3 delta, float radius = 0.25f)
    {
        var body = Body(centre, radius);
        Assert.True(Enclose()(body, delta, out MovementBodyQuery enclosed, out Vector3 endpoint, out double inflation));
        Assert.Equal(centre + delta, endpoint);
        BigInteger l1 = BigInteger.Abs(Units(centre.X) + Units(delta.X) - Units(endpoint.X)) +
            BigInteger.Abs(Units(centre.Y) + Units(delta.Y) - Units(endpoint.Y)) +
            BigInteger.Abs(Units(centre.Z) + Units(delta.Z) - Units(endpoint.Z));
        Assert.True(Units(enclosed.Radius) - Units(body.Radius) >= l1);
        float cylinderLength = 2f * (enclosed.HalfHeight - enclosed.Radius);
        Assert.True(Units(cylinderLength) / 2 >= Units(body.HalfHeight) - Units(body.Radius));
        Assert.True(Units(inflation) >= Units(enclosed.Radius) + Units(cylinderLength) / 2 - Units(body.HalfHeight));
        Assert.InRange(inflation, 0d, (double)0.001f);
        Assert.Equal(body.Centre, enclosed.Centre);
    }

    static MovementBodyQuery Body(Vector3 centre, float radius = 0.25f) => new(centre, radius, 0.75f, Room, null);
    static Type Helper => typeof(MovementQueryLease).Assembly.GetType("KhaozEngine.Locomotion.MovementCapsuleRounding")!;
    static EncloseDelegate Enclose() => Helper.GetMethod("TryEnclose", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<EncloseDelegate>();
    static ImpactDelegate Impact() => Helper.GetMethod("TryImpact", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<ImpactDelegate>();

    static BigInteger Units(double value)
    {
        Assert.True(double.IsFinite(value));
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int exponent = (int)((bits >> 52) & 0x7ff);
        BigInteger significand = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponent != 0) significand = (significand + (BigInteger.One << 52)) << (exponent - 1);
        return (bits >> 63) == 0 ? significand : -significand;
    }
}
