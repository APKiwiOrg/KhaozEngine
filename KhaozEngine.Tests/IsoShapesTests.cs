using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class IsoShapesTests
{
    [Fact]
    public void DiamondCorners_are_top_right_bottom_left_of_the_footprint()
    {
        Vector2[] c = IsoShapes.DiamondCorners(new Vector2(100f, 50f), 64f, 32f);
        Assert.Equal(new Vector2(100f, 34f), c[0]); // top  (centre.Y - h/2)
        Assert.Equal(new Vector2(132f, 50f), c[1]); // right (centre.X + w/2)
        Assert.Equal(new Vector2(100f, 66f), c[2]); // bottom
        Assert.Equal(new Vector2(68f, 50f), c[3]);  // left
    }

    [Fact]
    public void BlockFaces_lift_the_top_diamond_and_share_the_front_edges_with_the_ground()
    {
        var baseCenter = new Vector2(100f, 50f);
        (Vector2[] left, Vector2[] right) = IsoShapes.BlockFaces(baseCenter, 64f, 32f, height: 20f);

        Vector2[] ground = IsoShapes.DiamondCorners(baseCenter, 64f, 32f);
        Vector2[] top = IsoShapes.DiamondCorners(new Vector2(baseCenter.X, baseCenter.Y - 20f), 64f, 32f);

        // Left face: top-left, top-bottom, ground-bottom, ground-left.
        Assert.Equal(new[] { top[3], top[2], ground[2], ground[3] }, left);
        // Right face: top-bottom, top-right, ground-right, ground-bottom.
        Assert.Equal(new[] { top[2], top[1], ground[1], ground[2] }, right);

        // The bottom (front) vertex is shared by both faces and lifted by exactly the height.
        Assert.Equal(ground[2].Y - 20f, top[2].Y);
    }

    [Fact]
    public void EllipsePoints_are_foreshortened_2to1_and_start_at_the_rightmost_point()
    {
        Vector2[] p = IsoShapes.EllipsePoints(new Vector2(0f, 0f), radiusX: 40f, radiusY: 20f, segments: 4);
        Assert.Equal(4, p.Length);
        Assert.Equal(40f, p[0].X, 4);   // angle 0 -> rightmost
        Assert.Equal(0f, p[0].Y, 4);
        Assert.Equal(0f, p[1].X, 4);    // quarter turn -> bottom, radiusY down
        Assert.Equal(20f, p[1].Y, 4);
        Assert.Equal(-40f, p[2].X, 4);  // leftmost
    }

    [Fact]
    public void EllipsePoints_floors_segment_count_at_three()
    {
        Assert.Equal(3, IsoShapes.EllipsePoints(Vector2.Zero, 10f, 5f, segments: 1).Length);
    }
}
