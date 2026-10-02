using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileRaycastSharedGroundTests
{
    public static IEnumerable<object[]> ShapeCases()
    {
        foreach (TileOverlayShape shape in new[] { TileOverlayShape.Full, TileOverlayShape.DiagonalHalf,
            TileOverlayShape.CornerQuarter, TileOverlayShape.CornerThreeQuarter })
            for (int rotation = 0; rotation < 4; rotation++)
            {
                yield return new object[] { shape, rotation, false };
                yield return new object[] { shape, rotation, true };
            }
    }

    [Theory]
    [MemberData(nameof(ShapeCases))]
    public void BothRayFaces_HitCanonicalNoncoplanarGeometry(TileOverlayShape shape, int rotation, bool overlay)
    {
        TileWorldDocument doc = Tile(shape, rotation, overlay);
        doc.TileSize = 2f;
        doc.SetCornerHeightCm(3, 5, 0, -125);
        doc.SetCornerHeightCm(4, 5, 0, 250);
        doc.SetCornerHeightCm(3, 6, 0, 75);
        doc.SetCornerHeightCm(4, 6, 0, 325);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        Assert.True(TileGroundTriangles.TryDescribe(doc, 3, 5, 0, out TileGroundCell cell, triangles));

        for (int i = 0; i < cell.TriangleCount; i++)
        {
            Vector3 a = Position(doc, triangles[i].A);
            Vector3 b = Position(doc, triangles[i].B);
            Vector3 c = Position(doc, triangles[i].C);
            Assert.True(Vector3.Cross(b - a, c - a).Y > 0f);
            Vector3 expected = (a + b + c) / 3f;

            AssertHit(doc, expected + Vector3.UnitY * 10f, -Vector3.UnitY * 7f, expected, 10f);
            AssertHit(doc, expected - Vector3.UnitY * 10f, Vector3.UnitY * 7f, expected, 10f);
        }
    }

    [Theory]
    [InlineData(TileOverlayShape.CornerQuarter, 0, true, 0.25f)]
    [InlineData(TileOverlayShape.CornerQuarter, 1, true, 0.25f)]
    [InlineData(TileOverlayShape.CornerQuarter, 2, true, 0.25f)]
    [InlineData(TileOverlayShape.CornerQuarter, 3, true, 0.25f)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 1, true, 0.25f)]
    [InlineData(TileOverlayShape.CornerQuarter, 0, false, 0f)]
    [InlineData(TileOverlayShape.DiagonalHalf, 0, true, 0.5f)]
    [InlineData(TileOverlayShape.DiagonalHalf, 1, true, 0f)]
    [InlineData(TileOverlayShape.DiagonalHalf, 0, false, 0.5f)]
    [InlineData(TileOverlayShape.DiagonalHalf, 1, false, 0f)]
    public void RaisedNorthEastCorner_HasTheAuthoredFanOrForcedSplitHeight(
        TileOverlayShape shape, int rotation, bool overlay, float expectedHeight)
    {
        TileWorldDocument doc = Tile(shape, rotation, overlay);
        doc.SetCornerHeightCm(4, 6, 0, 100);

        AssertHit(doc, new Vector3(3.5f, 10f, -5.5f), -Vector3.UnitY,
            new Vector3(3.5f, expectedHeight, -5.5f), 10f - expectedHeight);
    }

    [Fact]
    public void NoDraw_IsDescribedForPickingButStillExcludedFromDrawnGround()
    {
        TileWorldDocument doc = Tile(TileOverlayShape.CornerQuarter, 0, true);
        doc.SetSettings(3, 5, 0, TileSettings.NoDraw);
        doc.SetCornerHeightCm(4, 6, 0, 100);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];

        Assert.False(TileGroundTriangles.TryDescribe(doc, 3, 5, 0, out _, triangles));
        Assert.True(TileGroundTriangles.TryDescribeIncludingNoDraw(doc, 3, 5, 0, out TileGroundCell cell, triangles));
        Assert.Equal(new TileGroundCell(TileOverlayShape.CornerQuarter, 0, false, 4), cell);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Sw, TileLatticePoint.MidS, TileLatticePoint.MidW, true),
            triangles[0]);
        Assert.Empty(TileGroundTriangles.Build(doc, new RegionCoord(0, 0), 0).Positions);
        AssertHit(doc, new Vector3(3.5f, 10f, -5.5f), -Vector3.UnitY, new Vector3(3.5f, 0.25f, -5.5f), 9.75f);
    }

    [Fact]
    public void NoDrawInclusiveDescription_StillRejectsVoidAndLeavesTheBufferUntouched()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetOverlay(3, 5, 0, 65000);
        doc.SetSettings(3, 5, 0, TileSettings.NoDraw);
        var sentinel = new TileLatticeTriangle(TileLatticePoint.Ne, TileLatticePoint.Ne, TileLatticePoint.Ne, true);
        var triangles = new TileLatticeTriangle[TileTriangulation.MaxTriangles];
        Array.Fill(triangles, sentinel);

        Assert.False(TileGroundTriangles.TryDescribeIncludingNoDraw(doc, 3, 5, 0, out TileGroundCell cell, triangles));
        Assert.Equal(default, cell);
        Assert.All(triangles, triangle => Assert.Equal(sentinel, triangle));
        Assert.Null(TileRaycast.Pick(doc, 0, new Vector3(3.5f, 10f, -5.5f), -Vector3.UnitY));
    }

    static TileWorldDocument Tile(TileOverlayShape shape, int rotation, bool overlay)
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetUnderlay(3, 5, 0, 1);
        doc.SetOverlay(3, 5, 0, overlay ? (ushort)65000 : (ushort)0);
        doc.SetOverlayShape(3, 5, 0, shape);
        doc.SetOverlayRotation(3, 5, 0, rotation);
        return doc;
    }

    static Vector3 Position(TileWorldDocument doc, TileLatticePoint point)
        => TileGroundTriangles.LatticePosition(doc, 3, 5, 0, point, 0, 0);

    static void AssertHit(TileWorldDocument doc, Vector3 origin, Vector3 direction, Vector3 expected, float distance)
    {
        TileHit? hit = TileRaycast.Pick(doc, 0, origin, direction);
        Assert.NotNull(hit);
        TileHit picked = hit!.Value;
        Assert.Equal((3, 5, 0), (picked.X, picked.Z, picked.Plane));
        Assert.True(Vector3.Distance(expected, picked.Point) < 0.00002f, picked.Point.ToString());
        Assert.Equal(distance, picked.Distance, 4);
    }
}
