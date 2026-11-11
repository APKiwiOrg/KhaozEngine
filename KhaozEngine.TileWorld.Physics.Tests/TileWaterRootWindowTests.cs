using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterRootWindowTests
{
    [Fact]
    public void ATangentBedPathCannotDischargeAnUnprovedDiagonalWindow()
    {
        // A path parallel to the bed, with the bottom core sphere tangent to its ideal plane.
        // The represented height band makes the diagonal crossing uncertain, while strict
        // positive bed contact inside that window cannot be proved.
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));
        var snapshot = field.CaptureTraceForTests();
        const float radius = 0.3f, half = 0.75f, x = 110.375f, z = -236.79904f;
        double grade = Math.Abs((double)(-1.3f) - (-0.35f));
        float centreY = (float)(snapshot.Ground.HeightAt(x, z) + ((double)half - radius) + radius * Math.Sqrt(1 + grade * grade));
        var query = new KhaozEngine.Locomotion.MovementMediumSweepQuery(
            new(new(x, centreY, z), radius, half, GrazingWaterBank.Space, null), new(0, (float)(grade * 0.4f), 0.4f));
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(), query);
        Assert.Equal(KhaozEngine.Locomotion.MovementAvailability.PrecisionExceeded, result.Before.Availability);
        Assert.Equal(result.Before, result.After);
        Assert.True(result.Proofs > 0);
        Assert.Equal(0, result.Certificates);
    }

    [Fact]
    public void ALocalDiagonalProofDoesNotClaimContactAfterTheRealBedExit()
    {
        var snapshot = GrazingWaterBank.Capture().Medium.CaptureTraceForTests();
        var piece = snapshot.Prisms[0];
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(piece, snapshot.Prisms[1]));
        var query = GrazingWaterBank.Lift();
        var start = piece.Start(query, Vector3.Zero);
        var drive = V.Point(0, 0.4f, 0);
        var core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        // The review's uncertain exit root is inside [0, 0.03]. Actual bed contact ends later.
        Assert.True(seam.Covers(2, 6, -1, start, drive, core, query.Body.Radius, 0, 0.03));
        Assert.False(seam.Covers(2, 6, -1, start, drive, core, query.Body.Radius, 0, 1));
    }

    [Theory]
    [InlineData(0.05, 0.2, 0.1, 0.2, 0)] // both windows are strictly covered, including their overlap
    [InlineData(0.05, 0.2, 0.7, 0.8, 1)] // a later root lies beyond actual bed contact
    [InlineData(0.05, 0.2, 0.7, 0.7005, 1)] // even a sub-budget unproved root retains its charge
    [InlineData(0.05, 0.2, 0.15, 0.3, 1)] // overlap does not lend a proof to the uncovered suffix
    [InlineData(0.3, 0.5, 0.7, 0.8, 2)] // neither window is in contact
    [InlineData(0.05, 0.25, 0.7, 0.8, 2)] // exact cap tangency at an endpoint is not positive contact
    public void EachUnprovedRootRetainsItsOwnFullCharge(double e0, double e1, double x0, double x1, int retained)
    {
        // On this flat bed the lower core sphere leaves contact exactly at t = 0.25.
        var (piece, seam) = FlatSeam();
        var roots = new TileWaterRootUncertainty(new(e0, e1), new(x0, x1));
        var start = V.Point(0.5, -0.5, -0.5) - piece.Reference;
        double charge = roots.CertifiedError(seam, 2, 6, -1, start, V.Point(0, 1, 0), I.Point(0.5), 0.25f, 1);
        double expected = retained == 0 ? 0 : retained == 1 ? roots.Exit.High - roots.Exit.Low : roots.Error(1);
        Assert.Equal(expected, charge);
        if (retained == 2) Assert.True(charge > KhaozEngine.Locomotion.MovementQueryLease.CoverageSkinMetres);
    }

    [Fact]
    public void ARealSideRootRemainsChargedEvenDuringContinuousBedContact()
    {
        var (piece, seam) = FlatSeam();
        var roots = new TileWaterRootUncertainty(new(0.05, 0.2), new(0.1, 0.2));
        var start = V.Point(0.5, -0.5, -0.5) - piece.Reference;
        Assert.True(seam.Covers(2, 6, -1, start, V.Point(0, 1, 0), I.Point(0.5), 0.25f, 0.05, 0.2));
        Assert.Equal(roots.Error(1), roots.CertifiedError(seam, 1, 6, -1, start, V.Point(0, 1, 0), I.Point(0.5), 0.25f, 1));
    }

    [Fact]
    public void ClampingPreservesTheClosedRootPortionAtBothQueryEndpoints()
    {
        var roots = TileWaterRootUncertainty.Clip(new(-1, 0.1), new(0.9, 2), 0, 1);
        Assert.Equal(new I(0, 0.1), roots.Entry);
        Assert.Equal(new I(0.9, 1), roots.Exit);
        var outside = TileWaterRootUncertainty.Clip(new(-2, -1), new(2, 3), 0, 1);
        Assert.Equal(new I(0, 0), outside.Entry);
        Assert.Equal(new I(1, 1), outside.Exit);
        Assert.Equal(0, outside.Error(1));
    }

    [Fact]
    public void ALaterRealBedDepartureKeepsItsOriginalContactsOrderAndError()
    {
        var field = GrazingWaterBank.Capture().Medium;
        var lift = GrazingWaterBank.Lift(110.5f);
        var query = new KhaozEngine.Locomotion.MovementMediumSweepQuery(lift.Body, new(0, 0.4f, 0));
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(), query);
        Assert.Equal(KhaozEngine.Locomotion.MovementAvailability.Known, result.Before.Availability);
        var snapshot = field.CaptureTraceForTests();
        var face = snapshot.Faces.First(f => f.Prism == 0 && f.Plane == 3);
        var piece = snapshot.Prisms[0];
        var start = piece.Start(query, Vector3.Zero);
        var drive = V.Point(0, query.Delta.Y, 0);
        var core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        var before = face.Shape.WithoutBedSeamForTests().Intersect(start, drive, core, query.Body.Radius,
            heightError: piece.HeightError, horizontalError: piece.HorizontalError);
        var after = face.Shape.Intersect(start, drive, core, query.Body.Radius,
            heightError: piece.HeightError, horizontalError: piece.HorizontalError);
        Assert.True(before.Hit && before.Exit > 0.03 && before.Exit < 1);
        Assert.True(before.Error > 0);
        Assert.Equal(before, after);
    }

    static (TileWaterPrism Piece, TileWaterBedSeam Seam) FlatSeam()
    {
        var column = TileWaterIndexEquivalenceTests.Column(0, new(0, -1, -1), new(1, 0, 0));
        var first = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
            new(0, -1, 0), new(1, -1, 0), new(1, -1, -1));
        var second = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw,
            new(0, -1, 0), new(1, -1, -1), new(0, -1, -1));
        var piece = new TileWaterPrism(column, first, 0, 0, 1, 0);
        var other = new TileWaterPrism(column, second, 0, 0, 1, 0);
        return (piece, Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(piece, other)));
    }
}
