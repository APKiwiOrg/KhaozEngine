using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileReachTests
{
    static TileCollisionMap Bake(TileWorldDocument doc) => TileMoveSimulatorTests.Bake(doc);

    [Fact]
    public void A_one_by_one_footprint_in_the_open_has_four_reach_tiles()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        IReadOnlyList<TileCoord> set = TileReach.Set(Bake(doc), new TileRect(10, 10, 1, 1), 0);
        Assert.Equal(4, set.Count);
        Assert.Contains(new TileCoord(9, 10, 0), set);
        Assert.Contains(new TileCoord(11, 10, 0), set);
        Assert.Contains(new TileCoord(10, 9, 0), set);
        Assert.Contains(new TileCoord(10, 11, 0), set);
        Assert.DoesNotContain(new TileCoord(9, 9, 0), set);   // a diagonal never reaches
    }

    [Fact]
    public void A_two_by_one_footprint_has_six_reach_tiles()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bench", 10, 10, 0, 1);                  // rotated: 2 wide, 1 deep
        IReadOnlyList<TileCoord> set = TileReach.Set(Bake(doc), new TileRect(10, 10, 2, 1), 0);
        Assert.Equal(6, set.Count);
        Assert.DoesNotContain(new TileCoord(10, 10, 0), set);   // never a footprint tile itself
        Assert.DoesNotContain(new TileCoord(11, 10, 0), set);
    }

    [Fact]
    public void A_two_by_two_footprint_has_eight_reach_tiles()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("rock_large", 10, 10, 0, 0);
        IReadOnlyList<TileCoord> set = TileReach.Set(Bake(doc), new TileRect(10, 10, 2, 2), 0);
        Assert.Equal(8, set.Count);
    }

    [Fact]
    public void A_wall_between_the_tile_and_the_footprint_denies_that_reach_tile()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        doc.AddObject("wall", 9, 10, 0, 2);                     // wall on the east edge of (9,10)
        IReadOnlyList<TileCoord> set = TileReach.Set(Bake(doc), new TileRect(10, 10, 1, 1), 0);
        Assert.DoesNotContain(new TileCoord(9, 10, 0), set);
        Assert.Equal(3, set.Count);
    }

    // Added on the task 4 self-review. The sibling above asserts the same DoesNotContain, and passes even with the
    // footprint skip deleted, because a Solid bench blocks its own tiles and CanStep refuses to step onto them
    // anyway. A footprint the collision map knows nothing about (an NPC, a ground item, an interactive archetype
    // with no collision) is the case that actually needs the skip, and this is the test that goes red without it.
    [Fact]
    public void An_unblocked_footprint_still_never_reaches_from_its_own_tiles()
    {
        TileCollisionMap map = Bake(TileMoveSimulatorTests.FlatWorld());
        IReadOnlyList<TileCoord> set = TileReach.Set(map, new TileRect(10, 10, 2, 1), 0);
        Assert.Equal(6, set.Count);
        Assert.DoesNotContain(new TileCoord(10, 10, 0), set);
        Assert.DoesNotContain(new TileCoord(11, 10, 0), set);
    }

    [Fact]
    public void A_fully_walled_object_has_no_reach_tile_at_all()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        foreach ((int x, int z) in new[] { (9, 10), (11, 10), (10, 9), (10, 11) }) doc.AddObject("tree", x, z, 0, 0);
        TileCollisionMap map = Bake(doc);
        Assert.Empty(TileReach.Set(map, new TileRect(10, 10, 1, 1), 0));
        Assert.False(TileReach.TryNearest(map, new TileRect(10, 10, 1, 1), 0, new TileCoord(5, 5, 0), 1, 64, out _, out _));
    }

    // Added on the task 4 self-review. TryNearest documents that it throws FindPath's nearest-reachable fallback
    // away, and no briefed case pinned it: the walled-in object above has an EMPTY reach set, so it returns false
    // before any search runs. Here the reach set is full and every tile in it is sealed off, which is the only
    // shape that reaches the Reached check. Drop that check and TryNearest reports success with a path that stops
    // outside the ring, so a player walks somewhere they cannot act from and the interaction silently never fires.
    [Fact]
    public void A_reach_tile_sealed_off_from_the_walker_is_not_offered_as_a_nearest()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        for (int z = 8; z <= 12; z++)
            for (int x = 8; x <= 12; x++)
                if (x == 8 || x == 12 || z == 8 || z == 12) doc.AddObject("tree", x, z, 0, 0);
        TileCollisionMap map = Bake(doc);

        var footprint = new TileRect(10, 10, 1, 1);
        Assert.Equal(4, TileReach.Set(map, footprint, 0).Count);   // in reach of the booth, just not of the walker
        Assert.False(TileReach.TryNearest(map, footprint, 0, new TileCoord(5, 5, 0), 1, 64, out _, out _));
    }

    [Fact]
    public void Nearest_takes_the_shortest_path_and_breaks_a_tie_by_scan_order()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileCollisionMap map = Bake(doc);

        Assert.True(TileReach.TryNearest(map, new TileRect(10, 10, 1, 1), 0, new TileCoord(5, 10, 0), 1, 64,
            out TileCoord west, out TilePath path));
        Assert.Equal(new TileCoord(9, 10, 0), west);
        Assert.Equal(4, path.Tiles.Count);

        // Dead centre south-west of the object: two candidates tie, and the scan order (z ascending, then x
        // ascending, then W, E, S, N) decides. The set's first entry among the tied wins.
        Assert.True(TileReach.TryNearest(map, new TileRect(10, 10, 1, 1), 0, new TileCoord(9, 9, 0), 1, 64,
            out TileCoord tie, out _));
        IReadOnlyList<TileCoord> set = TileReach.Set(map, new TileRect(10, 10, 1, 1), 0);
        TileCoord expected = set.First(c => c.Equals(new TileCoord(10, 9, 0)) || c.Equals(new TileCoord(9, 10, 0)));
        Assert.Equal(expected, tie);
    }

    [Fact]
    public void Standing_on_a_reach_tile_already_gives_an_empty_path_and_a_facing_into_the_footprint()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileCollisionMap map = Bake(doc);
        var from = new TileCoord(9, 10, 0);
        Assert.True(TileReach.Contains(map, new TileRect(10, 10, 1, 1), 0, from));
        Assert.True(TileReach.TryNearest(map, new TileRect(10, 10, 1, 1), 0, from, 1, 64, out TileCoord tile, out TilePath path));
        Assert.Equal(from, tile);
        Assert.Empty(path.Tiles);
        Assert.Equal(TileDirection.E, TileReach.FacingToward(map, new TileRect(10, 10, 1, 1), 0, from));
    }
}
