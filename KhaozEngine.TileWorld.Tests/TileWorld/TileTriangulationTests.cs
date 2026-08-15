using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The shared tile triangulation: how many triangles each shape cuts a tile into, which of them the
/// overlay paints, that they all wind the same way, and that they cover the tile exactly once.</summary>
public class TileTriangulationTests
{
    [Theory]
    [InlineData(TileOverlayShape.Full, 0, 2, 2)]
    [InlineData(TileOverlayShape.Full, 3, 2, 2)]
    [InlineData(TileOverlayShape.DiagonalHalf, 0, 2, 1)]
    [InlineData(TileOverlayShape.DiagonalHalf, 1, 2, 1)]
    [InlineData(TileOverlayShape.DiagonalHalf, 2, 2, 1)]
    [InlineData(TileOverlayShape.DiagonalHalf, 3, 2, 1)]
    [InlineData(TileOverlayShape.CornerQuarter, 0, 4, 1)]
    [InlineData(TileOverlayShape.CornerQuarter, 1, 4, 1)]
    [InlineData(TileOverlayShape.CornerQuarter, 2, 4, 1)]
    [InlineData(TileOverlayShape.CornerQuarter, 3, 4, 1)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 0, 4, 3)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 1, 4, 3)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 2, 4, 3)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 3, 4, 3)]
    public void Each_shape_cuts_the_tile_into_its_own_count_and_overlay_share(
        TileOverlayShape shape,
        int rotation,
        int expected,
        int painted)
    {
        Span<TileTriangle> triangles = stackalloc TileTriangle[TileTriangulation.MaxTriangles];
        int count = Cut(shape, rotation, triangles);

        Assert.Equal(expected, count);
        int overlay = 0;
        for (int i = 0; i < count; i++) if (triangles[i].Overlay) overlay++;
        Assert.Equal(painted, overlay);
    }

    [Theory]
    [InlineData(TileOverlayShape.Full)]
    [InlineData(TileOverlayShape.DiagonalHalf)]
    [InlineData(TileOverlayShape.CornerQuarter)]
    [InlineData(TileOverlayShape.CornerThreeQuarter)]
    public void Every_triangle_winds_the_same_way_at_every_rotation(TileOverlayShape shape)
    {
        // The corner fan is mirrored at an odd rotation. Nothing culls in the ground pass, so a triangle wound the
        // other way round is invisible there, but the shadow pass culls front faces and would drop it.
        Span<TileTriangle> triangles = stackalloc TileTriangle[TileTriangulation.MaxTriangles];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            int count = Cut(shape, rotation, triangles);
            for (int i = 0; i < count; i++)
                Assert.True(SignedArea(triangles[i]) > 0f, $"{shape} rotation {rotation} triangle {i} winds backwards");
        }
    }

    [Theory]
    [InlineData(TileOverlayShape.Full, 2f, 2f)]
    [InlineData(TileOverlayShape.DiagonalHalf, 1f, 2f)]
    [InlineData(TileOverlayShape.CornerQuarter, 0.25f, 2f)]
    [InlineData(TileOverlayShape.CornerThreeQuarter, 1.75f, 2f)]
    public void The_triangles_cover_the_tile_once_and_the_overlay_takes_its_share(
        TileOverlayShape shape,
        float paintedHalves,
        float totalHalves)
    {
        // Areas are counted in halves of the unit tile, so an eighth of a tile reads as 0.25 here.
        Span<TileTriangle> triangles = stackalloc TileTriangle[TileTriangulation.MaxTriangles];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            int count = Cut(shape, rotation, triangles);

            float total = 0f;
            float painted = 0f;
            for (int i = 0; i < count; i++)
            {
                float area = SignedArea(triangles[i]);
                total += area;
                if (triangles[i].Overlay) painted += area;
            }
            Assert.Equal(totalHalves, total, 1e-5f);
            Assert.Equal(paintedHalves, painted, 1e-5f);
        }
    }

    [Fact]
    public void A_mid_edge_point_lies_between_the_two_corners_it_averages()
    {
        AssertEnds(TilePoint.MidS, TilePoint.Sw, TilePoint.Se);
        AssertEnds(TilePoint.MidE, TilePoint.Se, TilePoint.Ne);
        AssertEnds(TilePoint.MidN, TilePoint.Nw, TilePoint.Ne);
        AssertEnds(TilePoint.MidW, TilePoint.Sw, TilePoint.Nw);

        // A corner is its own pair, so a caller can map every point the same way.
        TileTriangulation.Ends(TilePoint.Ne, out TilePoint first, out TilePoint second);
        Assert.Equal(TilePoint.Ne, first);
        Assert.Equal(TilePoint.Ne, second);
        Assert.Equal(new Vector2(1f, 1f), TileTriangulation.Local(TilePoint.Ne));
    }

    [Fact]
    public void A_span_too_small_for_the_widest_cut_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Overflowed());
    }

    static void Overflowed()
    {
        Span<TileTriangle> two = stackalloc TileTriangle[2];
        TileTriangulation.Triangulate(TileOverlayShape.Full, 0, true, two);
    }

    static void AssertEnds(TilePoint mid, TilePoint expectedFirst, TilePoint expectedSecond)
    {
        TileTriangulation.Ends(mid, out TilePoint first, out TilePoint second);
        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedSecond, second);
        Assert.Equal(
            (TileTriangulation.Local(expectedFirst) + TileTriangulation.Local(expectedSecond)) * 0.5f,
            TileTriangulation.Local(mid));
    }

    // The tile as the mesher and the raycast cut it: a flat tile, so the split rule only reflects the shape.
    static int Cut(TileOverlayShape shape, int rotation, Span<TileTriangle> into) =>
        TileTriangulation.Triangulate(shape, rotation, TileTriangulation.SplitSwNe(0, 0, 0, 0, shape, rotation), into);

    // Twice the triangle's area on the unit tile, positive when it winds counter-clockwise on x and z.
    static float SignedArea(TileTriangle t)
    {
        Vector2 a = TileTriangulation.Local(t.A);
        Vector2 b = TileTriangulation.Local(t.B);
        Vector2 c = TileTriangulation.Local(t.C);
        return (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
    }
}
