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

    [Theory]
    [InlineData(0, TileCollisionFlags.WallW | TileCollisionFlags.WallN | TileCollisionFlags.CornerNW, -1, 0, TileCollisionFlags.WallE, 0, 1, TileCollisionFlags.WallS, -1, 1, TileCollisionFlags.CornerSE)]
    [InlineData(1, TileCollisionFlags.WallN | TileCollisionFlags.WallE | TileCollisionFlags.CornerNE, 0, 1, TileCollisionFlags.WallS, 1, 0, TileCollisionFlags.WallW, 1, 1, TileCollisionFlags.CornerSW)]
    [InlineData(2, TileCollisionFlags.WallE | TileCollisionFlags.WallS | TileCollisionFlags.CornerSE, 1, 0, TileCollisionFlags.WallW, 0, -1, TileCollisionFlags.WallN, 1, -1, TileCollisionFlags.CornerNW)]
    [InlineData(3, TileCollisionFlags.WallS | TileCollisionFlags.WallW | TileCollisionFlags.CornerSW, 0, -1, TileCollisionFlags.WallN, -1, 0, TileCollisionFlags.WallE, -1, -1, TileCollisionFlags.CornerNE)]
    public void WallCorner_rotations_each_set_two_edges_two_mirrors_and_the_diagonal_corner(
        int rotation, TileCollisionFlags own,
        int dx1, int dz1, TileCollisionFlags mirror1,
        int dx2, int dz2, TileCollisionFlags mirror2,
        int cdx, int cdz, TileCollisionFlags cornerMirror)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("wall_corner", 10, 10, 0, rotation);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(own, map.Get(10, 10, 0));
        Assert.Equal(mirror1, map.Get(10 + dx1, 10 + dz1, 0));
        Assert.Equal(mirror2, map.Get(10 + dx2, 10 + dz2, 0));
        Assert.Equal(cornerMirror, map.Get(10 + cdx, 10 + cdz, 0));
    }

    [Fact]
    public void Solid_footprint_blocks_every_tile_rotated_and_diagonal_blocks_its_tile()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("rock_large", 20, 20, 0, 0);
        doc.AddObject("diag_wall", 30, 30, 0, 1);
        doc.AddObject("doorway", 31, 31, 0, 0);
        doc.AddObject("roof_flat", 32, 32, 0, 0);
        // bench is 1x2, so its two placements pin that a rotation actually swaps the footprint axes. They sit
        // at different anchors because two benches on one tile would block the union and prove nothing.
        doc.AddObject("bench", 40, 40, 0, 0);
        doc.AddObject("bench", 44, 44, 0, 1);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(20, 20, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(21, 21, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(22, 20, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(30, 30, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(31, 31, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(32, 32, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(40, 40, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(40, 41, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(41, 40, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(44, 44, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(45, 44, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(44, 45, 0));
    }

    [Fact]
    public void A_border_wall_does_not_open_the_missing_neighbour_region()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("wall", 0, 5, 0, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.WallW, map.Get(0, 5, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(-1, 5, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(-1, 0, 0));
        Assert.False(map.HasRegion(new RegionCoord(-1, 0)));
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
    public void Rebake_clears_a_removed_multi_tile_object_when_the_dirty_rect_covers_its_footprint()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileObject rock = doc.AddObject("rock_large", 20, 20, 0, 0);
        // Taken BEFORE the removal, which is the caller contract Rebake documents.
        TileRect footprint = TileFootprint.Of(Cat.Archetype("rock_large")!, 20, 20, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(21, 21, 0));
        doc.RemoveObject(rock.Id);
        TileCollisionBaker.Rebake(map, doc, Cat, footprint, 0);
        Assert.Equal(TileCollisionFlags.None, map.Get(20, 20, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(21, 20, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(20, 21, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(21, 21, 0));
    }

    [Fact]
    public void Rebake_gives_storage_to_a_region_added_after_the_bake()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        var added = new RegionCoord(1, 0);
        TileRect rect = added.Rect;
        doc.GetOrCreateRegion(added);
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++) doc.SetUnderlay(x, z, 0, 1);
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(70, 5, 0));
        // A dirty rect covering a corner of the new region, so the assertions below are about the region
        // getting its ground derived in full, not about the rect being re-derived.
        TileCollisionBaker.Rebake(map, doc, Cat, new TileRect(64, 0, 4, 4), 0);
        Assert.Equal(TileCollisionFlags.None, map.Get(70, 5, 0));
        Assert.Equal(TileCollisionFlags.None, map.Get(64, 0, 0));
        Assert.Equal(TileCollisionFlags.Blocked, map.Get(70, 5, 1));
        Assert.Equal(TileCollisionFlags.None, map.Get(100, 60, 0));
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
