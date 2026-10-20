using System;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedSweepPolynomialTests
{
    [Fact]
    public void QuadraticDoubleRootAtOneThirdIsClosedHit()
    {
        (double lower, double? upper) = First(9, -6, 1);
        Assert.NotNull(upper);
        Assert.True(lower <= 1d / 3 && upper.Value >= 1d / 3);
        Assert.InRange(upper.Value - lower, 0, 1d / 16384);
    }

    [Fact]
    public void QuadraticClosedEndpointAndWholeCellSeparationAreDistinct()
    {
        (double lower, double? upper) = First(1, -2, 1);
        Assert.NotNull(upper);
        Assert.True(lower <= 1 && upper.Value == 1);
        Assert.Null(First(1, -2, 1.0625).Upper);
        Assert.Equal((0d, (double?)0d), First(0, 0, 0));
        Assert.Null(First(0, 0, 0.0625).Upper);
    }

    [Fact]
    public void EnclosedRootCrossingACellBoundaryRetainsAContainingBracket()
    {
        var exact = new SweepExactWorkspace();
        const double uncertainty = 1d / (1 << 24);
        var regime = new SweepFeatureRegime(exact.Constant(0), exact.Constant(-1),
            exact.Enclosed(GeometryInterval.Enclose(0.5 - uncertainty, 0.5 + uncertainty)), [], 0);
        var work = new CapsuleSweepWork();
        Assert.True(CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, 1d / 16384,
            ref work, exact, out double lower, out double? upper));
        Assert.NotNull(upper);
        Assert.True(lower <= 0.5 - uncertainty && upper.Value >= 0.5 + uncertainty);
        Assert.InRange(upper.Value - lower, 0, 1d / 16384);
    }

    [Fact]
    public void KnownContactCarriesUncertainEarlierCellsWithoutInventingAWitness()
    {
        var exact = new SweepExactWorkspace();
        const double uncertainty = 1d / (1 << 24);
        var regime = new SweepFeatureRegime(exact.Constant(0), exact.Constant(-1),
            exact.Enclosed(GeometryInterval.Enclose(0.5 - uncertainty, 0.5 + uncertainty)), [], 0);
        var work = new CapsuleSweepWork();
        Assert.False(CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 0.5, 1d / 16384,
            ref work, exact, out _, out _));
        work = default;
        Assert.True(CapsuleSweepFeaturePolynomial.TryBeforeKnownContact(regime, 0.5, 1d / 16384,
            ref work, exact, out double lower, out double? upper));
        Assert.Null(upper);
        Assert.InRange(lower, 0.5 - uncertainty - 1d / 16384, 0.5 - uncertainty);
    }

    [Fact]
    public void SquaredOriginalOperandKeepsTransformUncertaintyCorrelated()
    {
        var exact = new SweepExactWorkspace();
        const double uncertainty = 1d / 65536;
        int start = exact.Enclosed(GeometryInterval.Enclose(-0.75 - uncertainty, -0.75 + uncertainty));
        int delta = exact.Constant(2), radiusSquared = exact.Constant(0.0625 * 0.0625);
        var regime = new SweepFeatureRegime(exact.Square(delta), exact.Multiply(exact.Constant(2), exact.Multiply(start, delta)),
            exact.Subtract(exact.Square(start), radiusSquared), [], 0,
            Distance: new SweepDistanceTerms(new(start, delta), default, default, radiusSquared, 1));
        var work = new CapsuleSweepWork();
        Assert.True(CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, 1d / 16384,
            ref work, exact, out double lower, out double? upper));
        Assert.NotNull(upper);
        Assert.True(lower <= 0.34375 - uncertainty / 2 && upper.Value >= 0.34375 + uncertainty / 2);
        Assert.InRange(upper.Value - lower, 0, 1d / 16384);
    }

    [Fact]
    public void ExhaustedPrimitiveCellsDepthAndExactStoragePublishNoBounds()
    {
        var exact = new SweepExactWorkspace();
        SweepFeatureRegime regime = SweepFeatureRegime.Polynomial(exact, 9, -6, 1);
        var work = new CapsuleSweepWork { Cells = 256 };
        Refused(regime, 1d / 16384, ref work, exact);
        work = default;
        Refused(regime, 1e-20, ref work, exact);
        Assert.InRange(work.Cells, 48, 256);
        exact.Reset();
        regime = SweepFeatureRegime.Polynomial(exact, 9, -6, 1);
        int tiny = exact.Square(exact.Square(exact.Constant(double.Epsilon)));
        exact.Add(tiny, exact.Constant(1));
        Assert.False(exact.IsResolved);
        work = default;
        Refused(regime, 1d / 16384, ref work, exact);
        bool complete = true;
        double lower = 1;
        double? upper = 1;
        long bytes = AllocationMeasurement.Measure(nameof(ExhaustedPrimitiveCellsDepthAndExactStoragePublishNoBounds), () =>
        {
            work = default;
            complete = CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, 1d / 16384,
                ref work, exact, out lower, out upper);
        });
        Assert.Equal(0, bytes);
        Assert.False(complete);
        Assert.Equal(0d, lower);
        Assert.Null(upper);
    }

    [Fact]
    public void ExactIntegerExponentOverflowRefusesInsteadOfWrapping()
    {
        SweepExactWorkspace.Integer left = default, right = default;
        left.Sign = right.Sign = 1;
        left.Length = right.Length = 1;
        left.Digits[0] = right.Digits[0] = 1;
        left.Exponent = int.MaxValue;
        right.Exponent = 1;
        Assert.False(BoundedGeometryArithmetic.TryMultiplySweepInteger(left, right, out _));
        left.Exponent = int.MinValue;
        right.Exponent = -1;
        Assert.False(BoundedGeometryArithmetic.TryMultiplySweepInteger(left, right, out _));
    }

    [Fact]
    public void ExactAlignmentAndNormalizationOverflowRefuseInsteadOfDroppingAnOperand()
    {
        SweepExactWorkspace.Integer left = default, right = default;
        left.Sign = right.Sign = 1;
        left.Length = right.Length = 1;
        left.Digits[0] = right.Digits[0] = 1;
        left.Exponent = int.MinValue;
        Assert.False(BoundedGeometryArithmetic.TryAddSweepInteger(left, right, out _));
        left.Exponent = right.Exponent = int.MaxValue;
        Assert.False(BoundedGeometryArithmetic.TryAddSweepInteger(left, right, out _));
    }

    static void Refused(in SweepFeatureRegime regime, double width, ref CapsuleSweepWork work, SweepExactWorkspace exact)
    {
        Assert.False(CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, width, ref work, exact,
            out double lower, out double? upper));
        Assert.Equal(0d, lower);
        Assert.Null(upper);
    }

    static (double Lower, double? Upper) First(double a, double b, double c)
    {
        var exact = new SweepExactWorkspace();
        SweepFeatureRegime regime = SweepFeatureRegime.Polynomial(exact, a, b, c);
        var work = new CapsuleSweepWork();
        Assert.True(CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, 1d / 16384,
            ref work, exact, out double lower, out double? upper));
        return (lower, upper);
    }
}
