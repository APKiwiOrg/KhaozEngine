using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileCollisionBakerTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    [Fact]
    public void Ground_rules_block_void_and_blocked_settings_and_missing_regions()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(3, 3, 0, 0);
        doc.SetSettings(4, 4, 0, TileSettings.Blocked);
        doc.SetSettings(5, 5, 0, TileSettings.Indoors);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(3, 3, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(4, 4, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(5, 5, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(-1, 0, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(5, 5, 9));
    }

    [Theory]
    [InlineData(0, TileCollisionFlags.WallW, -1, 0, TileCollisionFlags.WallE)]
    [InlineData(1, TileCollisionFlags.WallN, 0, 1, TileCollisionFlags.WallS)]
    [InlineData(2, TileCollisionFlags.WallE, 1, 0, TileCollisionFlags.WallW)]
    [InlineData(3, TileCollisionFlags.WallS, 0, -1, TileCollisionFlags.WallN)]
    public void Wall_sets_its_edge_and_mirrors_onto_the_neighbour(int rotation, TileCollisionFlags own, int dx, int dz, TileCollisionFlags mirrored)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("wall", 10, 10, 0, rotation);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(own, map.Get(10, 10, 0));
        Assert.Equal(mirrored, map.Get(10 + dx, 10 + dz, 0));
    }

    [Fact]
    public void Wall_mirrors_across_a_region_boundary()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.AddObject("wall", 63, 5, 0, 2);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.WallE, map.Get(63, 5, 0));
        Assert.Equal(TileCollisionFlags.WallW, map.Get(64, 5, 0));
    }

    [Fact]
    public void WallCorner_sets_two_edges_their_mirrors_and_the_diagonal_corner_bits()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("wall_corner", 10, 10, 0, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.WallW | TileCollisionFlags.WallN | TileCollisionFlags.CornerNW, map.Get(10, 10, 0));
        Assert.Equal(TileCollisionFlags.WallE, map.Get(9, 10, 0));
        Assert.Equal(TileCollisionFlags.WallS, map.Get(10, 11, 0));
        Assert.Equal(TileCollisionFlags.CornerSE, map.Get(9, 11, 0));
    }

    [Fact]
    public void Solid_footprint_blocks_every_tile_rotated_and_diagonal_blocks_its_tile()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("rock_large", 20, 20, 0, 0);
        doc.AddObject("diag_wall", 30, 30, 0, 1);
        doc.AddObject("doorway", 31, 31, 0, 0);
        doc.AddObject("roof_flat", 32, 32, 0, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(20, 20, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(21, 21, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(22, 20, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(30, 30, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(31, 31, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(32, 32, 0));
    }

    [Fact]
    public void Objects_only_affect_their_own_plane()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(5, 5, 1, 1);
        doc.SetUnderlay(6, 6, 1, 1);
        doc.AddObject("tree", 5, 5, 1, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.None, map.Get(5, 5, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(5, 5, 1));
        Assert.Equal(TileCollisionFlags.None, map.Get(6, 6, 1));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(7, 7, 1));
    }

    [Fact]
    public void Rebake_clears_a_removed_object_and_keeps_a_neighbouring_walls_mirror()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileObject tree = doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("wall", 12, 10, 0, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.WallE, map.Get(11, 10, 0));
        doc.RemoveObject(tree.Id);
        TileCollisionBaker.Rebake(map, doc, Cat, new TileRect(10, 10, 1, 1), 0);
        Assert.Equal(TileCollisionFlags.None, map.Get(10, 10, 0));
        Assert.Equal(TileCollisionFlags.WallE, map.Get(11, 10, 0));
        Assert.Equal(TileCollisionFlags.WallW, map.Get(12, 10, 0));
    }

    [Fact]
    public void Rebake_picks_up_a_ground_change_and_a_new_wall_at_the_rect_edge()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        doc.SetSettings(8, 8, 0, TileSettings.Blocked);
        doc.AddObject("wall", 9, 8, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Cat, new TileRect(8, 8, 1, 1), 0);
        Assert.Equal(TileCollisionFlags.Blocked | TileCollisionFlags.WallE, map.Get(8, 8, 0));
        Assert.Equal(TileCollisionFlags.WallW, map.Get(9, 8, 0));
    }
}
