using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterDomainTests
{
    static readonly MapLatticeFrame Up = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    static MapExactXz Xz(float x, float z) => new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z));
    static MapWaterDomain Rects(MapLatticeFrame frame, params MapWaterRect[] rects) =>
        MapWaterDomain.Compile(new(frame, rects, Array.Empty<MapWaterRing>()));
    static MapWaterDomain Ring(params (long X, long Z)[] points) => MapWaterDomain.Compile(new(Up, Array.Empty<MapWaterRect>(),
        new[] { new MapWaterRing(points.Select(p => MapLatticeAddress.Corner(p.X, p.Z)).ToArray()) }));

    [Fact]
    public void Rect_OwnsMinimumAndExcludesMaximum_IncludingNegativeZ()
    {
        // Lattice rows 3 to 7 run toward negative Z, so the world rectangle is x [2, 5) by z [-7, -3).
        MapWaterDomain d = Rects(MapLatticeFrame.ImportedMetreCentimetre, new MapWaterRect(2, 3, 5, 7));
        Assert.True(d.Contains(Xz(2, -7)));
        Assert.True(d.Contains(Xz(2, -5)));
        Assert.True(d.Contains(Xz(4.5f, -3.5f)));
        Assert.False(d.Contains(Xz(5, -7)));
        Assert.False(d.Contains(Xz(2, -3)));
        Assert.False(d.Contains(Xz(3, -3)));
    }

    [Fact]
    public void Polygon_NormalizesWindingAndOwnsEdgesByDirection()
    {
        MapWaterDomain clockwiseSquare = Ring((0, 0), (0, 4), (4, 4), (4, 0));
        MapWaterDomain lower = Ring((0, 0), (4, 0), (0, 4));
        MapWaterDomain upper = Ring((4, 0), (4, 4), (0, 4));
        foreach (var (x, z, inside) in new[] { (0f, 0f, true), (2f, 0f, true), (0f, 2f, true), (4f, 0f, false), (0f, 4f, false), (4f, 2f, false), (2f, 4f, false) })
            Assert.Equal(inside, clockwiseSquare.Contains(Xz(x, z)));
        Assert.False(lower.Contains(Xz(2, 2)));
        Assert.True(upper.Contains(Xz(2, 2)));
        Assert.True(lower.Contains(Xz(0, 0)));
        Assert.False(lower.Contains(Xz(4, 0)));
        Assert.False(upper.Contains(Xz(4, 0)));
    }

    [Fact]
    public void AdjacentPieces_OwnASharedSeamOnce()
    {
        var pieces = new[] { Ring((0, 0), (4, 0), (0, 4)), Ring((4, 0), (4, 4), (0, 4)), Rects(Up, new MapWaterRect(4, 0, 8, 4)) };
        for (int qx = 0; qx < 32; qx++)
            for (int qz = 0; qz < 16; qz++)
                Assert.Equal(1, pieces.Count(d => d.Contains(Xz(qx / 4f, qz / 4f))));
    }

    [Fact]
    public void Overlap_RefusesPositiveAreaAndAllowsTouching()
    {
        MapWaterDomain a = Rects(Up, new MapWaterRect(0, 0, 4, 4));
        Assert.False(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(4, 0, 8, 4))));
        Assert.False(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(4, 4, 8, 8))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(3, 0, 5, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(0, 0, 4, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(1, 1, 2, 2))));
        Assert.False(MapWaterDomain.OverlapsPositively(Ring((0, 0), (4, 0), (0, 4)), Ring((4, 0), (4, 4), (0, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(Ring((0, 0), (4, 0), (0, 4)), Rects(Up, new MapWaterRect(1, 1, 3, 3))));
    }

    [Fact]
    public void Placement_TransformsOnceUnderYawAndScale()
    {
        var placement = new MapWaterPlacement(new Vector3(10.23f, 5f, -3.17f), 0.371f, 1.137f);
        MapWaterDomain d = MapWaterDomain.Compile(new(Up, new[] { new MapWaterRect(0, 0, 2, 1) }, Array.Empty<MapWaterRing>()), placement);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, placement.Yaw);
        MapExactXz World(float x, float z)
        {
            Vector3 w = Vector3.Transform(new Vector3(x, 0, z) * placement.Scale, yaw) + placement.Position;
            return Xz(w.X, w.Z);
        }
        Assert.Equal(new[] { World(0, 0), World(2, 0), World(2, 1), World(0, 1) }.Order(), d.Pieces.Single().Ring.Order());
        Assert.True(d.Contains(World(1, 0.5f)));
        Assert.Equal(5f + 1.137f * 0.5f, placement.SurfaceY(0.5f));
    }

    [Fact]
    public void Placement_WithUnrepresentableCorners_Refuses()
    {
        var tiny = new MapWaterPlacement(Vector3.Zero, 0.371f, 1e-20f);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapWaterDomain.Compile(new(Up, new[] { new MapWaterRect(0, 0, 2, 1) }, Array.Empty<MapWaterRing>()), tiny));
        Assert.Contains("not representable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitAtHeight_NearThirtyTwoKilometres_KeepsExactArea()
    {
        static MapWideRational Q(long n, long d) => MapWideRational.From(new MapExactValue(n, d));
        var a = new MapWidePoint(Q(32000 * 61 + 1, 61), Q(-2, 5), Q(1, 61));
        var b = new MapWidePoint(Q(32001 * 63 + 1, 63), Q(-3, 10), Q(1, 63));
        var c = new MapWidePoint(Q(32000 * 59 + 1, 59), Q(-1, 5), Q(58, 59));
        MapWideRational surface = MapWideRational.From(MapExactValue.FromSingle(-0.37f));
        var (above, rest) = MapWaterPolygons.SplitAtHeight(new[] { a, b, c }, surface);
        Assert.Equal(WaterFixtures.WideArea(a, b, c), WaterFixtures.WideArea(above).Add(WaterFixtures.WideArea(rest)));
        Assert.All(above.Concat(rest).SelectMany(p => p), p => Assert.True(p.X.TryNarrow(out _) && p.Z.TryNarrow(out _)));
        Assert.Contains(above.SelectMany(p => p), p => p.Y.CompareTo(surface) == 0);
    }

    [Theory]
    [InlineData("bow-tie", "water body 'ocean': ring 0 self-intersects")]
    [InlineData("collinear", "water body 'ocean': ring 0 has zero area")]
    [InlineData("overlapping-pieces", "water body 'ocean': pieces 0 and 1 overlap")]
    [InlineData("overlapping-bodies", "water bodies 'cave-lake' and 'second-lake' overlap in space 'lake-cave'")]
    [InlineData("too-many-pieces", "water body 'ocean': domain exceeds 65536 pieces")]
    public void Validation_RefusesBadGeometry(string variant, string message)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(WaterFixtures.Broken(variant), MapDocRegistry.CreateDefault());
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void BodiesInDifferentSpaces_MayShareXz() =>
        Assert.Empty(MapDocumentValidator.Validate(WaterFixtures.OceanOverCaves(), MapDocRegistry.CreateDefault()));
}
