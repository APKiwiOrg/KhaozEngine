using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterBedSeamProofTests(ITestOutputHelper output)
{
    [Fact]
    public void ARoundedColumnMustStillSelectTheCertifiedBedTile()
    {
        // Binary32 division maps the last interior X of this 0.1 m column to tile 9.
        // The coplanar proof owns tile 8 and cannot borrow the neighbour's bed.
        const float size = 0.1f;
        float worldX = MathF.BitDecrement(9 * size);
        Assert.Equal(9, Math.Floor(TileWorldSpace.TileX(worldX, size)));
        var column = TileWaterIndexEquivalenceTests.Column(0, new(8 * size, -1, -size), new(9 * size, 0, 0));
        var first = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
            new(0, -1, 0), new(size, -1, 0), new(size, -1, -size));
        var second = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw,
            new(0, -1, 0), new(size, -1, -size), new(0, -1, -size));
        var a = new TileWaterPrism(column, first, 8, 0, size, 0);
        var b = new TileWaterPrism(column, second, 8, 0, size, 0);
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(a, b));
        Assert.False(seam.Covers(2, 6, -1, V.Point(worldX, -0.2501, -0.05) - a.Reference,
            V.Point(0, 0, 0), I.Point(0.5), 0.25f, 0, 1));
    }

    [Theory]
    [InlineData(-0.2501f, true)]
    [InlineData(-0.25f, false)]
    [InlineData(-0.2499f, false)]
    public void AnAnalyticCapTangencyCannotBecomeAStrictUnionContact(float centreY, bool positive)
    {
        // The bottom core endpoint is Y = centreY - 0.5. The bed is exactly -1 and
        // radius is exactly 0.25, so centreY = -0.25 is exact cap tangency.
        var column = TileWaterIndexEquivalenceTests.Column(0, new(0, -1, -1), new(1, 0, 0));
        var first = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
            new(0, -1, 0), new(1, -1, 0), new(1, -1, -1));
        var second = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw,
            new(0, -1, 0), new(1, -1, -1), new(0, -1, -1));
        var a = new TileWaterPrism(column, first, 0, 0, 1, 0);
        var b = new TileWaterPrism(column, second, 0, 0, 1, 0);
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(a, b));
        Assert.Equal(positive, seam.Covers(2, 6, -1, V.Point(0.5, centreY, -0.5), V.Point(0, 0, 0),
            I.Point(0.5), 0.25f, 0, 1));
    }

    [Fact]
    public void GrazingDiagonalChangesOnlyTheRedundantRootCharge()
    {
        // #1761. Keep the old solver callable with an immutable certificate-free shape copy.
        var snapshot = GrazingWaterBank.Capture().Medium.CaptureTraceForTests();
        var face = snapshot.Faces.First(face => face.Prism == 0 && face.Plane == 3);
        var piece = snapshot.Prisms[0];
        var query = GrazingWaterBank.Lift();
        var start = piece.Start(query, Vector3.Zero);
        var drive = V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
        var core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        var before = face.Shape.WithoutBedSeamForTests().Intersect(start, drive, core, query.Body.Radius,
            heightError: piece.HeightError, horizontalError: piece.HorizontalError);
        var after = face.Shape.Intersect(start, drive, core, query.Body.Radius,
            heightError: piece.HeightError, horizontalError: piece.HorizontalError);
        Assert.True(before.Error > MovementQueryLease.CoverageSkinMetres);
        Assert.InRange(after.Error, 0, MovementQueryLease.CoverageSkinMetres);
        Assert.Equal(before with { Error = after.Error }, after);
        output.WriteLine($"grazing bed face error before={before.Error:R}, after={after.Error:R} metres");
    }

    [Theory]
    [InlineData("continuous", true)]
    [InlineData("only-start-covered", false)]
    [InlineData("outside-column", false)]
    [InlineData("above-surface", false)]
    [InlineData("capsule-misses-bed", false)]
    [InlineData("real-side-basis", false)]
    [InlineData("real-corner-basis", false)]
    public void CertificateRequiresAnActualCapsuleContactOverTheWholeInterval(string kind, bool covered)
    {
        var snapshot = GrazingWaterBank.Capture().Medium.CaptureTraceForTests();
        var piece = snapshot.Prisms[0];
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(piece, snapshot.Prisms[1]));
        var query = GrazingWaterBank.Lift();
        var start = piece.Start(query, Vector3.Zero);
        var drive = V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
        if (kind == "only-start-covered") drive = V.Point(2, 0, 0);
        if (kind == "outside-column") start += V.Point(-1, 0, 0);
        if (kind == "above-surface") start += V.Point(0, 2, 0);
        if (kind == "capsule-misses-bed") start += V.Point(0, -2, 0);
        Assert.Equal(covered, seam.Covers(kind == "real-side-basis" ? 1 : 2, 6,
            kind == "real-corner-basis" ? 4 : -1, start, drive,
            I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius), query.Body.Radius, 0, 1));
    }

    [Theory]
    [InlineData("crease")]
    [InlineData("domain")]
    [InlineData("bed-id")]
    [InlineData("surface")]
    [InlineData("column-owner")]
    public void AnActualBoundaryCannotAcquireACoplanarSeamCertificate(string kind)
    {
        var snapshot = GrazingWaterBank.Capture().Medium.CaptureTraceForTests();
        var piece = snapshot.Prisms[0];
        var other = snapshot.Prisms[1];
        Assert.NotNull(TileWaterBedSeam.Create(piece, other));
        var triangle = other.Triangle;
        var column = other.Column;
        if (kind == "crease") triangle = triangle with { C = triangle.C + new Vector3(0, 0.01f, 0) };
        if (kind == "domain") column = column with { Domain = new("water-world", "different-water") };
        if (kind == "bed-id") column = column with { BedBoundaryId = "different-bed" };
        if (kind == "surface") column = column with { Maximum = column.Maximum + Vector3.UnitY };
        var changed = new TileWaterPrism(column, triangle, other.TileX, other.TileZ, other.TileSize,
            other.ColumnIndex + (kind == "column-owner" ? 1 : 0));
        Assert.Null(TileWaterBedSeam.Create(piece, changed));
    }

    [Fact]
    public void AnAxisOutsideWaterStillRequiresItsWetCapsuleFootprint()
    {
        var field = GrazingWaterBank.Capture().Medium;
        var query = GrazingWaterBank.Lift(109.9f);
        Assert.False(field.SampleCentreWater(query.Body, Vector3.Zero).InWater);
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(), query);
        Assert.Equal(MovementAvailability.Known, result.After.Availability);
        Assert.True(result.After.ContactsWritten > 0);
        Assert.Equal(0, result.Certificates);
    }
}
