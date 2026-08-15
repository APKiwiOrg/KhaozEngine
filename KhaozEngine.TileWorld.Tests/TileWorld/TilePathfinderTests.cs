using System;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TilePathfinderTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileCollisionMap Map(TileWorldDocument? doc = null, params (string archetype, int x, int z, int rot)[] objects)
    {
        doc ??= TileWorldTestData.FlatWorld();
        foreach (var (a, x, z, r) in objects) doc.AddObject(a, x, z, 0, r);
        return TileCollisionBaker.Bake(doc, Cat);
    }

    static TilePath Find(TileCollisionMap map, int sx, int sz, int gx, int gz, int size = 1, int radius = 64) =>
        TilePathfinder.FindPath(map, 0, new TileCoord(sx, sz, 0), new TileCoord(gx, gz, 0), size, radius);

    [Fact]
    public void Straight_open_path_is_the_diagonal_shortcut_and_reaches()
    {
        TilePath p = Find(Map(), 5, 5, 8, 8);
        Assert.True(p.Reached);
        Assert.Equal(3, p.Tiles.Count);
        Assert.Equal(new TileCoord(8, 8, 0), p.End);
    }

    [Fact]
    public void Same_start_and_goal_is_an_empty_reached_path()
    {
        TilePath p = Find(Map(), 5, 5, 5, 5);
        Assert.True(p.Reached);
        Assert.Empty(p.Tiles);
        Assert.Equal(new TileCoord(5, 5, 0), p.End);
    }

    [Fact]
    public void A_wall_line_is_walked_around_never_through()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < 20; z++) doc.AddObject("wall", 10, z, 0, 2);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        TilePath p = Find(map, 8, 5, 12, 5);
        Assert.True(p.Reached);
        Assert.True(p.Tiles.Count >= 4 + 15);
        for (int i = 0; i < p.Tiles.Count; i++)
        {
            TileCoord a = i == 0 ? new TileCoord(8, 5, 0) : p.Tiles[i - 1];
            TileCoord b = p.Tiles[i];
            if (a.X == 10 && b.X == 11) Assert.True(a.Z >= 20);
        }
    }

    [Fact]
    public void Unreachable_goal_yields_the_nearest_reachable_tile()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int i = 29; i <= 33; i++) { doc.AddObject("tree", i, 29, 0, 0); doc.AddObject("tree", i, 33, 0, 0); }
        for (int i = 30; i <= 32; i++) { doc.AddObject("tree", 29, i, 0, 0); doc.AddObject("tree", 33, i, 0, 0); }
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        TilePath p = Find(map, 5, 31, 31, 31);
        Assert.False(p.Reached);
        Assert.Equal(new TileCoord(28, 31, 0), p.End);
    }

    [Fact]
    public void Same_inputs_give_the_same_tiles_and_the_search_is_bounded()
    {
        TileCollisionMap map = Map(objects: new[] { ("tree", 20, 20, 0), ("tree", 21, 21, 0), ("wall", 25, 20, 1) });
        TilePath a = Find(map, 5, 5, 40, 40);
        TilePath b = Find(map, 5, 5, 40, 40);
        Assert.Equal(a.Tiles, b.Tiles);
        TilePath bounded = Find(map, 5, 5, 40, 40, radius: 8);
        Assert.False(bounded.Reached);
        Assert.Equal(new TileCoord(13, 13, 0), bounded.End);
    }

    [Fact]
    public void A_start_on_a_blocked_tile_still_walks_out()
    {
        // The tree blocks the tile the agent stands on. CanStep allows egress, so the search has to proceed
        // normally rather than refusing to move off a tile something was dropped on top of.
        TilePath p = Find(Map(objects: new[] { ("tree", 5, 5, 0) }), 5, 5, 8, 8);
        Assert.True(p.Reached);
        Assert.Equal(new TileCoord(8, 8, 0), p.End);
    }

    [Fact]
    public void A_radius_outside_the_accepted_range_is_refused()
    {
        TileCollisionMap map = Map();
        Assert.Throws<ArgumentOutOfRangeException>(() => Find(map, 5, 5, 8, 8, radius: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Find(map, 5, 5, 8, 8, radius: TilePathfinder.MaxSearchRadius + 1));
    }

    [Fact]
    public void A_2x2_agent_avoids_a_gap_it_does_not_fit_through()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < 64; z++) if (z != 10) doc.AddObject("tree", 20, z, 0, 0);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, Cat);
        Assert.True(Find(map, 5, 10, 30, 10).Reached);
        Assert.False(Find(map, 5, 10, 30, 10, size: 2).Reached);
    }
}
