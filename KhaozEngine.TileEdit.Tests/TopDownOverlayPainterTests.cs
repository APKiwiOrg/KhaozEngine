using System;
using System.Collections.Generic;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Tests for the overlay painter on its own: no GPU, no capture, just a hand-filled RGBA buffer and
/// pixels checked by coordinate. This is where the tile-to-pixel mapping is pinned, including the flip that
/// makes row 0 the NORTHERNMOST tile, because a render test can only say the bytes changed.</summary>
public class TopDownOverlayPainterTests
{
    const int PxPerTile = 4;
    static readonly TileRect Rect = new(0, 0, 4, 4);
    const int Size = 4 * PxPerTile;

    static byte[] Buffer(byte fill)
    {
        var rgba = new byte[Size * Size * 4];
        Array.Fill(rgba, fill);
        return rgba;
    }

    static (byte R, byte G, byte B, byte A) Pixel(byte[] rgba, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    // A world whose one region is grass everywhere, with the blocking and the wall this class paints over.
    static (TileWorldDocument Doc, TileCollisionMap Collision) World()
    {
        var doc = new TileWorldDocument { Id = "overlay", DisplayName = "Overlay", PlaneCount = 1 };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, 1);
        return (doc, new TileCollisionMap(1));
    }

    [Fact]
    public void Parse_TakesTheKnownOverlaysAndRefusesTheRest()
    {
        Assert.Empty(TopDownOverlayPainter.Parse(null));
        Assert.Empty(TopDownOverlayPainter.Parse("  "));
        Assert.Equal(new[] { "grid", "objects" }, TopDownOverlayPainter.Parse(" Grid , objects , grid "));

        ArgumentException ex = Assert.Throws<ArgumentException>(() => TopDownOverlayPainter.Parse("grid,heat"));
        Assert.Contains("collision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grid_DrawsOneBlendedLineOnEveryTileEdge()
    {
        (TileWorldDocument doc, TileCollisionMap collision) = World();
        byte[] rgba = Buffer(255);

        TopDownOverlayPainter.Paint(rgba, Size, Size, Rect, 0, PxPerTile, doc, collision, new[] { "grid" });

        // Black at 0.35 over white: 255 + (0 - 255) * 0.35 = 165.75, rounded to 166. Sampled where exactly one
        // line runs, since a crossing is blended by both of them.
        Assert.Equal<(byte, byte, byte, byte)>((166, 166, 166, 255), Pixel(rgba, 0, 2));
        Assert.Equal<(byte, byte, byte, byte)>((166, 166, 166, 255), Pixel(rgba, PxPerTile, 7));
        Assert.Equal<(byte, byte, byte, byte)>((166, 166, 166, 255), Pixel(rgba, Size - 1, 3));
        // Inside a tile, untouched.
        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 2, 2));
    }

    [Fact]
    public void Collision_TintsBlockedTilesAndLinesWalledEdges()
    {
        (TileWorldDocument doc, TileCollisionMap collision) = World();
        collision.EnsureRegion(new RegionCoord(0, 0));
        // Tile (1, 1) is blocked, tile (2, 3) carries a north wall. Row 0 of the image is z = 3.
        collision.Or(1, 1, 0, TileCollisionFlags.Blocked);
        collision.Or(2, 3, 0, TileCollisionFlags.WallN);
        byte[] rgba = Buffer(255);

        TopDownOverlayPainter.Paint(rgba, Size, Size, Rect, 0, PxPerTile, doc, collision, new[] { "collision" });

        // Tile (1, 1) spans columns 4..7 and rows 8..11 (z 1 is the third band from the top).
        // (200, 40, 40) at 0.4 over white is (233, 169, 169).
        Assert.Equal<(byte, byte, byte, byte)>((233, 169, 169, 255), Pixel(rgba, 5, 9));
        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 5, 13));
        // The north edge of tile (2, 3) is the TOP row of the topmost band, drawn solid.
        Assert.Equal<(byte, byte, byte, byte)>((220, 40, 40, 255), Pixel(rgba, 9, 0));
        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 9, 1));
    }

    [Fact]
    public void Objects_DotTheAnchorTileOfTheQueriedPlane()
    {
        (TileWorldDocument doc, TileCollisionMap collision) = World();
        doc.AddObject("tree", 0, 3, 0, 0);
        doc.AddObject("tree", 3, 0, 0, 0);
        byte[] rgba = Buffer(0);

        TopDownOverlayPainter.Paint(rgba, Size, Size, Rect, 0, PxPerTile, doc, collision, new[] { "objects" });

        (byte r, byte g, byte b) = TopDownOverlayPainter.ObjectColor("tree");
        // The north-west object sits in the top-left band, centred on (2, 2) with a three pixel dot.
        Assert.Equal<(byte, byte, byte, byte)>((r, g, b, 255), Pixel(rgba, 2, 2));
        Assert.Equal<(byte, byte, byte, byte)>((r, g, b, 255), Pixel(rgba, 1, 1));
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), Pixel(rgba, 4, 4));
        // The south-east object sits in the bottom-right band, which is the z flip doing its job.
        Assert.Equal<(byte, byte, byte, byte)>((r, g, b, 255), Pixel(rgba, 14, 14));
    }

    [Fact]
    public void Regions_DrawTheBordersOnTheRectEdgesTheyFallOn()
    {
        (TileWorldDocument doc, TileCollisionMap collision) = World();
        byte[] rgba = Buffer(0);

        // The rect starts at the origin, so its west edge and its south edge are both region borders.
        TopDownOverlayPainter.Paint(rgba, Size, Size, Rect, 0, PxPerTile, doc, collision, new[] { "regions" });

        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 0, 8));
        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 1, 8));
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), Pixel(rgba, 2, 8));
        // The south border is nudged back inside the image rather than drawn past its last row.
        Assert.Equal<(byte, byte, byte, byte)>((255, 255, 255, 255), Pixel(rgba, 8, Size - 1));
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), Pixel(rgba, 8, Size - 3));
    }

    [Fact]
    public void Paint_RefusesABufferThatDoesNotMatchTheRect()
    {
        (TileWorldDocument doc, TileCollisionMap collision) = World();
        IReadOnlyList<string> grid = TopDownOverlayPainter.Parse("grid");

        Assert.Throws<ArgumentException>(() =>
            TopDownOverlayPainter.Paint(Buffer(0), Size, Size, new TileRect(0, 0, 3, 4), 0, PxPerTile, doc, collision, grid));
        Assert.Throws<ArgumentException>(() =>
            TopDownOverlayPainter.Paint(new byte[16], Size, Size, Rect, 0, PxPerTile, doc, collision, grid));
        Assert.Throws<ArgumentException>(() =>
            TopDownOverlayPainter.Paint(Buffer(0), Size, Size, new TileRect(0, 0, 0, 0), 0, PxPerTile, doc, collision, grid));
    }
}
