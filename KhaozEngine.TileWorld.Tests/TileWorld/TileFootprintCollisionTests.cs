using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Standing, stepping and pathing for an NxN agent anchored on its south-west tile. Wall rotation: 0 W,
/// 1 N, 2 E, 3 S edge of the placed tile, mirrored onto the neighbour.</summary>
public class TileFootprintCollisionTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileCollisionMap Map(params (string archetype, int x, int z, int rot)[] objects)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        foreach (var (a, x, z, r) in objects) doc.AddObject(a, x, z, 0, r);
        return TileCollisionBaker.Bake(doc, Cat);
    }

    static TilePath Find(TileCollisionMap map, int sx, int sz, int gx, int gz, int size) =>
        TilePathfinder.FindPath(map, 0, new TileCoord(sx, sz, 0), new TileCoord(gx, gz, 0), size);

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void Open_ground_stands_every_size(int n) =>
        Assert.True(TileCollision.CanStand(Map(), 10, 10, 0, n));

    [Fact]
    public void A_blocked_tile_anywhere_in_the_footprint_refuses_standing()
    {
        TileCollisionMap map = Map(("tree", 11, 11, 0));
        Assert.False(TileCollision.CanStand(map, 10, 10, 0, 2));
        Assert.True(TileCollision.CanStand(map, 12, 10, 0, 2));
    }

    [Fact]
    public void A_wall_between_two_tiles_of_the_footprint_refuses_standing_but_not_a_one_tile_body()
    {
        TileCollisionMap map = Map(("wall", 10, 10, 2));            // east edge of (10,10)
        Assert.False(TileCollision.CanStand(map, 10, 10, 0, 2));
        Assert.True(TileCollision.CanStand(map, 10, 10, 0, 1));
        Assert.True(TileCollision.CanStand(map, 11, 10, 0, 2));    // the wall is on this footprint's west boundary
    }

    [Fact]
    public void A_two_by_two_cannot_walk_along_a_fence_line_it_straddles()
    {
        TileCollisionMap map = Map(("fence", 10, 12, 2));           // east edge of (10,12)
        Assert.False(TileCollision.CanStep(map, 10, 11, 0, TileDirection.N, 2));
        Assert.True(TileCollision.CanStep(map, 10, 11, 0, TileDirection.N, 1));
    }

    [Fact]
    public void A_region_the_map_does_not_hold_refuses_standing() =>
        Assert.False(TileCollision.CanStand(Map(), 63, 63, 0, 2));   // (64, 64) is outside region (0,0)

    /// <summary>A band of trees from x 20 to 24 over the whole region height, open only on rows 9, 10 and 11, with
    /// walls on both long sides of row 10. Every row of the band is a one tile corridor, so a one tile body walks
    /// through. A 2x2 body fits the band's three open rows only by straddling one of the two walls, which the per
    /// tile step alone would let it do.</summary>
    [Fact]
    public void A_one_tile_corridor_walled_both_sides_passes_a_one_tile_body_but_not_a_two_by_two()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int x = 20; x <= 24; x++)
        {
            for (int z = 0; z < TileRegion.Size; z++)
                if (z < 9 || z > 11) doc.AddObject("tree", x, z, 0, 0);
            doc.AddObject("wall", x, 9, 0, 1);                       // north edge of (x,9), south edge of (x,10)
            doc.AddObject("wall", x, 10, 0, 1);                      // north edge of (x,10), south edge of (x,11)
        }
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);

        Assert.True(Find(map, 5, 10, 30, 10, 1).Reached);
        Assert.False(Find(map, 5, 10, 30, 10, 2).Reached);
    }

    /// <summary>A wall line on the east edge of x 20 over the whole region height, with rows 10 and 11 left open as
    /// a two tile doorway.</summary>
    [Fact]
    public void A_two_wide_doorway_passes_a_two_by_two_but_not_a_three_by_three()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            if (z != 10 && z != 11) doc.AddObject("wall", 20, z, 0, 2);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);

        Assert.True(Find(map, 5, 10, 30, 10, 2).Reached);
        Assert.False(Find(map, 5, 10, 30, 10, 3).Reached);
    }

    [Fact]
    public void A_three_by_three_path_is_deterministic_and_stands_on_every_anchor()
    {
        TileCollisionMap map = Map(("tree", 20, 20, 0), ("tree", 21, 24, 0), ("wall", 25, 20, 1),
            ("fence", 30, 30, 2), ("tree", 12, 14, 0));

        TilePath a = Find(map, 5, 5, 40, 40, 3);
        TilePath b = Find(map, 5, 5, 40, 40, 3);

        Assert.True(a.Reached);
        Assert.NotEmpty(a.Tiles);
        Assert.Equal(a.Tiles, b.Tiles);
        foreach (TileCoord t in a.Tiles) Assert.True(TileCollision.CanStand(map, t.X, t.Z, 0, 3));
    }
}
