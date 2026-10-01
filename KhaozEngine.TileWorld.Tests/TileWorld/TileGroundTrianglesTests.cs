using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The GPU-free full-detail ground triangle rule: which tiles draw, how each one is cut and split, and where
/// its lattice points sit. Every expected value here is worked out from the rule by hand. The parity with the ground
/// mesher is proved in the render tests, which can reach the mesher.</summary>
public sealed class TileGroundTrianglesTests
{
    const ushort Road = 6;
    const int Tiles = TileRegion.Size * TileRegion.Size;

    [Fact]
    public void AFlatTileIsTwoTrianglesAtItsHeight()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, 150);
        TileWorldCatalogs catalogs = TileWorldTestData.EditingCatalogs();

        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, 3, 5, 0, out TileGroundCell cell, triangles));
        Assert.Equal(new TileGroundCell(TileOverlayShape.Full, 0, true, 2), cell);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne, true),
                     triangles[0]);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw, true),
                     triangles[1]);

        TileGroundMesh mesh = TileGroundTriangles.Build(doc, catalogs, new RegionCoord(0, 0), 0);
        Assert.Equal(new RegionCoord(0, 0), mesh.Region);
        Assert.Equal(0, mesh.Plane);
        Assert.Equal(Tiles * 6, mesh.Positions.Length);
        Assert.Equal(Tiles * 6, mesh.Indices.Length);
        for (int i = 0; i < mesh.Indices.Length; i++) Assert.Equal(i, mesh.Indices[i]);

        // Tile (3, 5) is the 5 * 64 + 3 th tile in z-then-x order, six vertices each, and every vertex sits at
        // 1.5 metres with world z negated against tile z.
        int first = (5 * TileRegion.Size + 3) * 6;
        Vector3[] expected =
        [
            new(3f, 1.5f, -5f), new(4f, 1.5f, -5f), new(4f, 1.5f, -6f),
            new(3f, 1.5f, -5f), new(4f, 1.5f, -6f), new(3f, 1.5f, -6f),
        ];
        Assert.Equal(expected, mesh.Positions.AsSpan(first, 6).ToArray());
    }

    [Fact]
    public void ANoDrawTileAndAnUnderlayZeroTileAreSkipped()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(1, 1, 0, TileSettings.NoDraw);
        doc.SetUnderlay(2, 1, 0, 0);
        TileWorldCatalogs catalogs = TileWorldTestData.EditingCatalogs();

        Assert.False(TileGroundTriangles.IsDrawable(doc, 1, 1, 0));
        Assert.False(TileGroundTriangles.IsDrawable(doc, 2, 1, 0));
        Assert.True(TileGroundTriangles.IsDrawable(doc, 3, 1, 0));

        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        Assert.False(TileGroundTriangles.TryDescribe(doc, catalogs, 1, 1, 0, out TileGroundCell noDraw, triangles));
        Assert.Equal(default, noDraw);
        Assert.False(TileGroundTriangles.TryDescribe(doc, catalogs, 2, 1, 0, out TileGroundCell bare, triangles));
        Assert.Equal(default, bare);

        TileGroundMesh mesh = TileGroundTriangles.Build(doc, catalogs, new RegionCoord(0, 0), 0);
        Assert.Equal((Tiles - 2) * 6, mesh.Positions.Length);

        // Tile (0, 1) is drawn, then (1, 1) and (2, 1) are skipped, so (3, 1) follows straight on from it.
        int afterRow = (1 * TileRegion.Size + 1) * 6;
        Assert.Equal(new Vector3(0f, 0f, -1f), mesh.Positions[afterRow - 6]);
        Assert.Equal(new Vector3(3f, 0f, -1f), mesh.Positions[afterRow]);

        TileGroundMesh empty = TileGroundTriangles.Build(doc, catalogs, new RegionCoord(0, 0), 1);
        Assert.Empty(empty.Positions);
        Assert.Empty(empty.Indices);
    }

    [Fact]
    public void ASlopedTileSplitsOnTheDiagonalTheMesherChooses()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileWorldCatalogs catalogs = TileWorldTestData.EditingCatalogs();
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];

        // SE and NW raised: the SW to NE diagonal differs by 0, the other by 0 too, and a tie goes SW to NE.
        doc.SetCornerHeightCm(5, 4, 0, 100);
        doc.SetCornerHeightCm(4, 5, 0, 100);
        Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, 4, 4, 0, out TileGroundCell tie, triangles));
        Assert.True(tie.SplitSwNe);

        // SW raised alone: SW to NE differs by 200, NW to SE by 0, so the split runs NW to SE.
        doc.SetCornerHeightCm(10, 10, 0, 200);
        Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, 10, 10, 0, out TileGroundCell sw, triangles));
        Assert.Equal(new TileGroundCell(TileOverlayShape.Full, 0, false, 2), sw);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Nw, true),
                     triangles[0]);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Se, TileLatticePoint.Ne, TileLatticePoint.Nw, true),
                     triangles[1]);

        // A diagonal half with no overlay material does not cut, but its odd rotation still forces NW to SE on
        // ground that would otherwise tie to SW to NE.
        doc.SetOverlayShape(20, 20, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(20, 20, 0, 1);
        Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, 20, 20, 0, out TileGroundCell forced, triangles));
        Assert.Equal(new TileGroundCell(TileOverlayShape.Full, 1, false, 2), forced);
    }

    [Fact]
    public void ACutOverlayEmitsFourTrianglesWithMidpointCorners()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetCornerHeightCm(7, 6, 0, 100);
        doc.SetCornerHeightCm(6, 7, 0, 200);
        doc.SetCornerHeightCm(7, 7, 0, 300);
        doc.SetOverlay(6, 6, 0, Road);
        doc.SetOverlayShape(6, 6, 0, TileOverlayShape.CornerQuarter);
        TileWorldCatalogs catalogs = TileWorldTestData.EditingCatalogs();

        // SW to NE differs by 300, NW to SE by 100, so the split runs NW to SE, which a corner cut ignores.
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        Assert.True(TileGroundTriangles.TryDescribe(doc, catalogs, 6, 6, 0, out TileGroundCell cell, triangles));
        Assert.Equal(new TileGroundCell(TileOverlayShape.CornerQuarter, 0, false, 4), cell);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.Sw, TileLatticePoint.MidS, TileLatticePoint.MidW, true),
                     triangles[0]);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.MidS, TileLatticePoint.Se, TileLatticePoint.Ne, false),
                     triangles[1]);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.MidS, TileLatticePoint.Ne, TileLatticePoint.Nw, false),
                     triangles[2]);
        Assert.Equal(new TileLatticeTriangle(TileLatticePoint.MidS, TileLatticePoint.Nw, TileLatticePoint.MidW, false),
                     triangles[3]);

        Vector3 sw = new(6f, 0f, -6f);
        Vector3 se = new(7f, 1f, -6f);
        Vector3 nw = new(6f, 2f, -7f);
        Vector3 ne = new(7f, 3f, -7f);
        Vector3 midS = new(6.5f, 0.5f, -6f);
        Vector3 midW = new(6f, 1f, -6.5f);
        Assert.Equal(sw, Position(doc, TileLatticePoint.Sw));
        Assert.Equal(se, Position(doc, TileLatticePoint.Se));
        Assert.Equal(nw, Position(doc, TileLatticePoint.Nw));
        Assert.Equal(ne, Position(doc, TileLatticePoint.Ne));
        Assert.Equal(midS, Position(doc, TileLatticePoint.MidS));
        Assert.Equal(midW, Position(doc, TileLatticePoint.MidW));

        TileGroundMesh mesh = TileGroundTriangles.Build(doc, catalogs, new RegionCoord(0, 0), 0);
        Assert.Equal((Tiles + 1) * 6, mesh.Positions.Length);
        int first = (6 * TileRegion.Size + 6) * 6;
        Vector3[] expected = [sw, midS, midW, midS, se, ne, midS, ne, nw, midS, nw, midW];
        Assert.Equal(expected, mesh.Positions.AsSpan(first, 12).ToArray());
    }

    static Vector3 Position(TileWorldDocument doc, TileLatticePoint point) =>
        TileGroundTriangles.LatticePosition(doc, 6, 6, 0, point, 0, 0);
}
