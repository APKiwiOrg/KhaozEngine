using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelSeamMatrixTests
{
    [Fact]
    public void RoundedOwnTileFailureIsRetainedByTheFrozenFaceEvaluation()
    {
        // #1755 and the existing own-tile proof regression. Division rounds this interior coordinate into tile 9.
        const float size = 0.1f;
        float worldX = MathF.BitDecrement(9 * size);
        Assert.Equal(9, Math.Floor(TileWorldSpace.TileX(worldX, size)));
        var column = TileWaterIndexEquivalenceTests.Column(0, new(8 * size, -1, -size), new(9 * size, 0, 0));
        var a = new TileWaterPrism(column, new(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
            new(0, -1, 0), new(size, -1, 0), new(size, -1, -size)), 8, 0, size, 0);
        var b = new TileWaterPrism(column, new(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw,
            new(0, -1, 0), new(size, -1, -size), new(0, -1, -size)), 8, 0, size, 0);
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(a, b));
        var start = V.Point(worldX, -0.2501, -0.05) - a.Reference;
        var drive = V.Point(0, 0, 0);
        Assert.False(seam.Covers(2, 6, -1, start, drive, I.Point(0.5), 0.25f, 0, 1));
        var shape = new TileWaterConvexShape([.. a.Planes, a.Planes[3].Opposite], seam);
        var frozen = new FrozenTileWaterConvexShape(shape);
        FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0.5), 0.25f),
            shape.Intersect(start, drive, I.Point(0.5), 0.25f));
    }

    [Theory]
    [InlineData("grazing")]
    [InlineData("departure")]
    [InlineData("real-side")]
    [InlineData("crease")]
    [InlineData("outside-owner")]
    public void BankAndRootWindowFixturesMatchTheFrozenShapeAndTrace(string kind)
    {
        var document = GrazingWaterBank.Document(109, 3);
        if (kind == "crease") document.SetCornerHeightCm(110, 237, 0, -125);
        var field = MovementWaterFieldTests.Field(document);
        var geometry = field.CaptureTraceForTests();
        var query = GrazingWaterBank.Lift(kind == "outside-owner" ? 108.9f : 110.375f);
        if (kind == "departure") query = new(query.Body, new(0, 0.4f, 0));
        if (kind == "real-side") query = new(query.Body, new(0.4f, 0.2f, 0));
        _ = FrozenWaterComparison.Check(field, new(geometry), query);
        Assert.NotEmpty(geometry.Faces);
        foreach (var face in geometry.Faces)
        {
            var piece = geometry.Prisms[face.Prism];
            var frozen = new FrozenTileWaterConvexShape(face.Shape);
            var start = piece.Start(query, Vector3.Zero);
            var drive = V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
            var core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
            foreach (var window in new[] { (0d, 1d), (0d, 0.03d), (0.05d, 0.2d), (0.7d, 0.8d), (0.25d, 0.25d) }.Reverse())
                FrozenWaterComparison.Range(frozen.Intersect(start, drive, core, query.Body.Radius,
                    window.Item1, window.Item2, piece.HeightError, piece.HorizontalError), face.Shape.Intersect(start, drive,
                    core, query.Body.Radius, window.Item1, window.Item2, piece.HeightError, piece.HorizontalError));
        }
    }

    [Theory]
    [InlineData(false, 0.05, 0.2, 0.7, 0.8, 1)] // Entry only.
    [InlineData(true, 0.05, 0.2, 0.8, 0.9, 1)] // Exit only.
    [InlineData(false, 0.05, 0.2, 0.15, 0.23, 0)] // Overlapping, both independently proved.
    [InlineData(false, 0.05, 0.2, 0.15, 0.3, 1)] // Overlap does not discharge the unproved suffix.
    public void EachClampedRootUsesOnlyItsOwnSeamProof(bool descending, double e0, double e1,
        double x0, double x1, int retained)
    {
        var piece = WaterKernelTraceFixture.Piece();
        var second = WaterKernelTraceFixture.Piece(second: true);
        var seam = Assert.IsType<TileWaterBedSeam>(TileWaterBedSeam.Create(piece, second));
        var roots = TileWaterRootUncertainty.Clip(new(e0, e1), new(x0, x1), 0, 1);
        var start = V.Point(0.5, descending ? 0.5 : -0.5, -0.5);
        var drive = V.Point(0, descending ? -1 : 1, 0);
        double expected = retained == 0 ? 0 : descending ? e1 - e0 : x1 - x0;
        Assert.Equal(expected, roots.CertifiedError(seam, 2, 6, -1, start, drive, I.Point(0.5), 0.25f, 1));
        var clipped = TileWaterRootUncertainty.Clip(new(-1, 0.1), new(0.9, 2), 0, 1);
        Assert.Equal(new I(0, 0.1), clipped.Entry);
        Assert.Equal(new I(0.9, 1), clipped.Exit);
        Assert.Equal(roots.Error(1), roots.CertifiedError(seam, 1, 6, -1, start, drive, I.Point(0.5), 0.25f, 1));
        Assert.False(seam.Covers(2, 6, -1, start + V.Point(2, 0, 0), drive, I.Point(0.5), 0.25f, 0, 1));
        var otherOwner = new TileWaterPrism(second.Column with { Domain = new("water-world", "other-owner") },
            second.Triangle, second.TileX, second.TileZ, second.TileSize, second.ColumnIndex);
        Assert.Null(TileWaterBedSeam.Create(piece, otherOwner));
    }
}
