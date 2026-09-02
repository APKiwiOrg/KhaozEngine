using System;
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

    [Fact]
    public void An_actor_on_another_plane_is_neither_in_reach_nor_a_zero_step_arrival()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileCollisionMap map = Bake(doc);
        var footprint = new TileRect(10, 10, 1, 1);
        var upstairs = new TileCoord(9, 10, 1);                 // the reach tile in x and z, one plane up

        Assert.False(TileReach.Contains(map, footprint, 0, upstairs));
        Assert.False(TileReach.TryNearest(map, footprint, 0, upstairs, 1, 64, out TileCoord tile, out TilePath path));
        Assert.Equal(default(TileCoord), tile);               // no tile offered, so nothing to walk to
        Assert.Empty(path.Tiles);
    }
    // THE ADMISSION BOUND, pinned at its exact edge, because the whole value of it is that it refuses only what
    // the eight searches below it would have refused anyway. FindPath's window is a (2r+1)^2 box centred on the
    // start, and every reach tile is one tile closer than the footprint it belongs to, so a footprint at exactly
    // maxRadius + 1 still has one candidate inside the window and must still resolve.
    [Fact]
    public void A_footprint_one_tile_past_the_search_window_still_resolves_and_two_tiles_past_it_does_not()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileCollisionMap map = Bake(doc);
        var from = new TileCoord(20, 20, 0);
        const int radius = 64;

        // 65 tiles away: its west reach tile is at exactly 64, the last column the window holds.
        Assert.True(TileReach.TryNearest(map, new TileRect(20 + radius + 1, 20, 1, 1), 0, from, 1, radius,
            out TileCoord tile, out TilePath path));
        Assert.Equal(new TileCoord(20 + radius, 20, 0), tile);
        Assert.Equal(radius, path.Tiles.Count);

        // 66 tiles away: every one of its four reach tiles is outside the window, so there is nothing to find.
        Assert.False(TileReach.TryNearest(map, new TileRect(20 + radius + 2, 20, 1, 1), 0, from, 1, radius,
            out _, out _));
    }
}

/// <summary>
/// The allocation half of the reach search, serialized in the AllocSensitive collection so byte counting is not
/// disturbed by parallel test threads, and split from <see cref="TileReachTests"/> so the behavioural tests there
/// keep the assembly's parallelism.
/// </summary>
[Collection("AllocSensitive")]
public class TileReachAllocationTests
{
    /// <summary>
    /// A target the search window cannot hold costs NOTHING, which is the admission bound this is really about.
    /// Net ids are handed out from a counter, so a hostile client can name a target it has never seen by guessing
    /// a small integer, and every such guess used to buy up to eight <c>TilePathfinder.FindPath</c> calls at the
    /// player simulator's radius of 64: a 129x129 int plus byte scratch each, about 83 KB, BFS-flooded to
    /// exhaustion before failing. One crafted command per tick per slot, for as many slots as the attacker holds.
    /// </summary>
    [Fact]
    public void A_target_outside_the_search_window_costs_no_search_at_all()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(2, 2));
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var from = new TileCoord(20, 20, 0);
        var far = new TileRect(150, 150, 1, 1);                // 130 tiles away, twice the search radius

        // Warm the JIT on the shape before anything is measured, and prove the answer is the refusal either way.
        for (int i = 0; i < 8; i++)
            Assert.False(TileReach.TryNearest(map, far, 0, from, 1, 64, out _, out _));

        // The BEST of several passes, for the reason TileRemoteReadAllocationTests states at length: the per
        // thread counter is only accurate to one allocation context, and a background collection that retires a
        // context inside the window charges bytes this thread never allocated. A call that really searched would
        // allocate on every pass and cannot hide behind the minimum.
        const int iterations = 16;
        const int passes = 5;
        long allocated = long.MaxValue;
        for (int pass = 0; pass < passes; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++) TileReach.TryNearest(map, far, 0, from, 1, 64, out _, out _);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(allocated < 4096,
            $"the refused reach search allocated {allocated} bytes over {iterations} calls, which is a search");
    }
}
