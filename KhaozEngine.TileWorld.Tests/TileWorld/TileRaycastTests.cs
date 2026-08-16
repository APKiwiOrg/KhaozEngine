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
        // Neither case above separates <= from ==. The first is a tie (0 vs 0) and the second is a strict
        // greater, so both hold either way. These two pin the rest of the rule: a strictly flatter SW-NE
        // diagonal wins, and a strictly flatter NW-SE one loses.
        Assert.True(TileTriangulation.SplitSwNe(0, 0, 100, 0, TileOverlayShape.Full, 0));
        Assert.False(TileTriangulation.SplitSwNe(0, 50, 50, 100, TileOverlayShape.Full, 0));
        Assert.True(TileTriangulation.SplitSwNe(0, 0, 0, 100, TileOverlayShape.DiagonalHalf, 2));
        Assert.False(TileTriangulation.SplitSwNe(0, 100, 100, 0, TileOverlayShape.DiagonalHalf, 1));
    }

    [Fact]
    public void Straight_down_hits_the_tile_under_the_origin_at_its_height()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        // World z is MINUS tile z, so the centre of tile (10, 10) is world (10.5, -10.5). A +10.5 world z would
        // land on tile z -11, which is the mirror the seam exists to keep out of the render.
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(10.5f, 50f, -10.5f), new Vector3(0, -1, 0));
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
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(0.5f, 6f, -5.5f), new Vector3(1, -0.05f, 0));
        Assert.NotNull(hit);
        Assert.Equal(20, hit!.Value.X);
        Assert.Equal(5, hit.Value.Z);
        Assert.Equal(5f, hit.Value.Point.Y, 2);
    }

    [Fact]
    public void An_oblique_ray_at_a_non_unit_tile_size_measures_in_world_units()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.TileSize = 2f;
        for (int x = 20; x <= 21; x++) for (int z = 5; z <= 6; z++) doc.SetCornerHeightCm(x, z, 0, 500);
        // Origin is in world units, so this is tile x 0.5, tile z 5.5. The slope is half the unit-size test's:
        // a tile is two world units wide now, so 0.05 would drop below the 5 m plateau early and hit the ramp
        // in tile 19. At 0.025 the ray is y = 6 - 0.025 * (wx - 1), so 5.025 at wx 40 (clearing tile 19's ramp
        // by 25 mm) and crossing y = 5 at wx 41, inside tile 20, which spans wx 40 to 42.
        var origin = new Vector3(1f, 6f, -11f);
        TileHit? hit = TileRaycast.Pick(doc, 0, origin, new Vector3(1f, -0.025f, 0f));
        Assert.NotNull(hit);
        Assert.Equal(20, hit!.Value.X);
        Assert.Equal(5, hit.Value.Z);
        Assert.Equal(5f, hit.Value.Point.Y, 2);
        Assert.Equal(41f, hit.Value.Point.X, 1);
        // The distance is what pins world units. sqrt(40^2 + 1^2) is 40.01, a tile-unit distance would be 20.
        Assert.Equal(Vector3.Distance(origin, hit.Value.Point), hit.Value.Distance, 3);
        Assert.InRange(hit.Value.Distance, 39.96f, 40.06f);
    }

    [Fact]
    public void A_ray_marching_north_walks_toward_increasing_tile_z()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int x = 5; x <= 6; x++) for (int z = 20; z <= 21; z++) doc.SetCornerHeightCm(x, z, 0, 500);
        // The z half of the DDA, which the x-marching cases above never touch. North is -world z, so a ray aimed
        // at -z climbs toward HIGHER tile z, and the step sign has to follow the tile-space direction rather than
        // the world one. Same shape as the oblique x test: the ray clears tile z 19's ramp by 25 mm and crosses
        // the 5 m plateau inside tile z 20.
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(5.5f, 6f, -0.5f), new Vector3(0f, -0.05f, -1f));
        Assert.NotNull(hit);
        Assert.Equal(5, hit!.Value.X);
        Assert.Equal(20, hit.Value.Z);
        Assert.Equal(5f, hit.Value.Point.Y, 2);
        Assert.Equal(-20.5f, hit.Value.Point.Z, 1);
    }

    [Fact]
    public void A_corner_cut_is_picked_on_the_fan_the_mesher_draws()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetOverlay(10, 10, 0, 2);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.CornerQuarter);
        doc.SetOverlayRotation(10, 10, 0, 0);
        doc.SetCornerHeightCm(11, 11, 0, 100);

        // The cut fans the tile from its south mid-edge point, which puts the centre on the triangle
        // (MidS, NE, NW), a quarter of the way up the raised corner. The plain pair would report 0 there, which
        // is the whole reason the raycast goes through the same triangulation the mesher draws.
        TileHit? hit = TileRaycast.Pick(doc, 0, new Vector3(10.5f, 50f, -10.5f), new Vector3(0, -1, 0));
        Assert.NotNull(hit);
        Assert.Equal((10, 10), (hit!.Value.X, hit.Value.Z));
        Assert.Equal(0.25f, hit.Value.Point.Y, 3);

        // Take the material away and the shape has nothing to cut with, so the tile picks as the plain pair.
        doc.SetOverlay(10, 10, 0, 0);
        TileHit? plain = TileRaycast.Pick(doc, 0, new Vector3(10.5f, 50f, -10.5f), new Vector3(0, -1, 0));
        Assert.NotNull(plain);
        Assert.Equal(0f, plain!.Value.Point.Y, 3);
    }

    [Fact]
    public void Void_tiles_and_missing_regions_are_not_hit()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(10, 10, 0, 0);
        Assert.Null(TileRaycast.Pick(doc, 0, new Vector3(10.5f, 5f, -10.5f), new Vector3(0, -1, 0)));
        Assert.Null(TileRaycast.Pick(doc, 0, new Vector3(-10.5f, 5f, -10.5f), new Vector3(0, -1, 0)));
    }

    [Fact]
    public void Plane_lift_is_honoured_and_TileSize_scales_world_units()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(4, 4, 1, 1);
        TileHit? hit = TileRaycast.Pick(doc, 1, new Vector3(4.5f, 10f, -4.5f), new Vector3(0, -1, 0));
        Assert.Equal(3f, hit!.Value.Point.Y, 3);
        doc.TileSize = 2f;
        TileHit? scaled = TileRaycast.Pick(doc, 0, new Vector3(21f, 10f, -21f), new Vector3(0, -1, 0));
        Assert.Equal((10, 10), (scaled!.Value.X, scaled.Value.Z));
    }
}
