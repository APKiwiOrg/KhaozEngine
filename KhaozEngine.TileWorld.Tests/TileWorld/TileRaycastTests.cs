using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileRaycastTests
{
    [Fact]
    public void Split_rule_prefers_the_flatter_diagonal_and_overlays_force_it()
    {
        Assert.True(TileTriangulation.SplitSwNe(0, 100, 100, 0, TileOverlayShape.Full, 0));
        Assert.False(TileTriangulation.SplitSwNe(0, 0, 0, 100, TileOverlayShape.Full, 0));
        Assert.True(TileTriangulation.SplitSwNe(0, 0, 0, 100, TileOverlayShape.DiagonalHalf, 2));
        Assert.False(TileTriangulation.SplitSwNe(0, 100, 100, 0, TileOverlayShape.DiagonalHalf, 1));
    }

    [Fact]
    public void Straight_down_hits_the_tile_under_the_origin_at_its_height()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(10.5f, 50f, 10.5f), new Vector3(0, -1, 0));
        Assert.NotNull(hit);
        Assert.Equal((10, 10, 0), (hit!.Value.X, hit.Value.Z, hit.Value.Plane));
        Assert.Equal(0f, hit.Value.Point.Y, 3);
        Assert.Equal(50f, hit.Value.Distance, 3);
    }

    [Fact]
    public void An_oblique_ray_hits_the_first_tile_along_its_path_and_respects_height()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int x = 20; x <= 21; x++) for (int z = 5; z <= 6; z++) doc.SetCornerHeightCm(x, z, 0, 500);
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(0.5f, 6f, 5.5f), new Vector3(1, -0.05f, 0));
        Assert.NotNull(hit);
        Assert.Equal(20, hit!.Value.X);
        Assert.Equal(5, hit.Value.Z);
        Assert.Equal(5f, hit.Value.Point.Y, 2);
    }

    [Fact]
    public void Void_tiles_and_missing_regions_are_not_hit()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(10, 10, 0, 0);
        Assert.Null(TileRaycast.Pick(doc, 0, new Vector3(10.5f, 5f, 10.5f), new Vector3(0, -1, 0)));
        Assert.Null(TileRaycast.Pick(doc, 0, new Vector3(-10.5f, 5f, 10.5f), new Vector3(0, -1, 0)));
    }

    [Fact]
    public void Plane_lift_is_honoured_and_TileSize_scales_world_units()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(4, 4, 1, 1);
        TileHit? hit = TileRaycast.Pick(doc, 1, new Vector3(4.5f, 10f, 4.5f), new Vector3(0, -1, 0));
        Assert.Equal(3f, hit!.Value.Point.Y, 3);
        doc.TileSize = 2f;
        TileHit? scaled = TileRaycast.Pick(doc, 0, new Vector3(21f, 10f, 21f), new Vector3(0, -1, 0));
        Assert.Equal((10, 10), (scaled!.Value.X, scaled.Value.Z));
    }
}
