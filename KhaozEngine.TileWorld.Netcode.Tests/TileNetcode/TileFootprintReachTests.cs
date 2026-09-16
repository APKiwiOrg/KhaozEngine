using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>Reach for an NxN agent anchored on its south-west tile against an MxM target footprint anchored at
/// (20, 20). Wall rotation: 0 W, 1 N, 2 E, 3 S edge of the placed tile, mirrored onto the neighbour.</summary>
public class TileFootprintReachTests
{
    const int WindowMin = 14, WindowMax = 26;

    static TileCollisionMap OpenMap() => TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());

    static TileRect Target(int m) => new(20, 20, m, m);

    public static TheoryData<int, int> Pairings()
    {
        var data = new TheoryData<int, int>();
        for (int a = 1; a <= 3; a++) for (int t = 1; t <= 3; t++) data.Add(a, t);
        return data;
    }

    // Geometry alone, independent of TileReach: the rects do not overlap and share a cardinal edge.
    static bool InRangeOnOpenGround(TileRect a, TileRect target)
    {
        bool overlap = !a.Intersect(target).IsEmpty;
        bool xTouch = (a.X1 == target.X || target.X1 == a.X) && a.Z < target.Z1 && a.Z1 > target.Z;
        bool zTouch = (a.Z1 == target.Z || target.Z1 == a.Z) && a.X < target.X1 && a.X1 > target.X;
        return !overlap && (xTouch || zTouch);
    }

    static List<TileCoord> BruteForceAnchors(int n, int m)
    {
        var found = new List<TileCoord>();
        for (int z = WindowMin; z <= WindowMax; z++)
        for (int x = WindowMin; x <= WindowMax; x++)
            if (InRangeOnOpenGround(new TileRect(x, z, n, n), Target(m))) found.Add(new TileCoord(x, z, 0));
        return found;
    }

    static IEnumerable<TileCoord> Sorted(IEnumerable<TileCoord> tiles) => tiles.OrderBy(t => t.Z).ThenBy(t => t.X);

    // Brute force over a window: in range exactly when the rects do not overlap and share a cardinal edge.
    [Theory, MemberData(nameof(Pairings))]
    public void Contains_is_edge_adjacency_without_overlap_on_open_ground(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        TileRect target = Target(m);
        for (int z = WindowMin; z <= WindowMax; z++)
        for (int x = WindowMin; x <= WindowMax; x++)
            Assert.Equal(InRangeOnOpenGround(new TileRect(x, z, n, n), target),
                TileReach.Contains(map, target, 0, new TileCoord(x, z, 0), n));
    }

    [Theory, MemberData(nameof(Pairings))]
    public void Set_lists_exactly_the_in_range_anchors_without_repeats(int n, int m)
    {
        IReadOnlyList<TileCoord> set = TileReach.Set(OpenMap(), Target(m), 0, n);
        List<TileCoord> expected = BruteForceAnchors(n, m);

        Assert.Equal(Sorted(expected), Sorted(set));
        Assert.Equal(set.Count, set.Distinct().Count());
        Assert.Equal(4 * (m + n - 1), set.Count);
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void Size_one_set_is_the_legacy_set_in_the_legacy_order(int m)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("wall", 19, 20, 0, 2);                        // east edge of (19,20), denies that reach tile
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);

        IReadOnlyList<TileCoord> legacy = TileReach.Set(map, Target(m), 0);
        Assert.DoesNotContain(new TileCoord(19, 20, 0), legacy);   // the wall really broke the symmetry
        Assert.Equal(legacy, TileReach.Set(map, Target(m), 0, 1));
    }

    // The order above size 1, pinned element for element. Each one tile reach tile, in its own W, E, S, N scan order,
    // offers the anchors whose 2x2 holds it (dz ascending, then dx ascending), skipping an overlap and a repeat.
    [Fact]
    public void Size_two_set_against_a_two_by_two_is_in_the_pinned_order()
    {
        IReadOnlyList<TileCoord> set = TileReach.Set(OpenMap(), Target(2), 0, 2);
        var expected = new[]
        {
            (18, 20), (18, 19), (20, 18), (19, 18), (22, 20), (22, 19),
            (21, 18), (18, 21), (20, 22), (19, 22), (22, 21), (21, 22),
        }.Select(p => new TileCoord(p.Item1, p.Item2, 0));
        Assert.Equal(expected, set);
    }

    // Design section 6.1's candidate loop, transcribed from the spec rather than from the shipped code, so the
    // order and the dedupe are pinned against what the document says instead of against themselves.
    static List<TileCoord> SpecOrder(TileCollisionMap map, TileRect target, int n, out int emitted)
    {
        emitted = 0;
        var listed = new List<TileCoord>();
        foreach (TileCoord p in TileReach.Set(map, target, 0))          // the one tile set, in its existing order
            for (int dz = 0; dz < n; dz++)                              // z ascending, then x ascending
                for (int dx = 0; dx < n; dx++)
                {
                    var a = new TileCoord(p.X - dx, p.Z - dz, 0);
                    emitted++;
                    if (!new TileRect(a.X, a.Z, n, n).Intersect(target).IsEmpty) continue;   // rect(a, N) overlaps T
                    if (listed.Contains(a)) continue;                                        // already listed
                    listed.Add(a);
                }
        return listed;
    }

    // The dedupe is the point: above size 1 several reach tiles produce the same anchor, and only the FIRST
    // occurrence is kept. A cheaper dedupe must not reorder, drop or admit anything, on open ground or behind walls.
    [Theory, MemberData(nameof(Pairings))]
    public void Set_lists_the_anchors_in_the_order_design_section_six_one_states(int n, int m)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("wall", 19, 20, 0, 2);                            // east edge of (19,20)
        doc.AddObject("tree", 21, 19, 0, 0);                            // Blocked, south of (21,20)
        TileCollisionMap walled = TileMoveSimulatorTests.Bake(doc);
        TileRect target = Target(m);

        foreach (TileCollisionMap map in new[] { OpenMap(), walled })
        {
            List<TileCoord> expected = SpecOrder(map, target, n, out int produced);
            Assert.Equal(expected, TileReach.Set(map, target, 0, n));
            if (n > 1) Assert.True(produced > expected.Count, "the loop really produced a repeat to dedupe");
        }
    }

    // Set and Contains agree on a map where walls and a Blocked tile really deny reach. Walls deny a reach tile by its
    // edge, the tree denies the tile it stands on, and a wall further east only matters to a 3x3 target.
    [Theory, MemberData(nameof(Pairings))]
    public void Set_and_Contains_agree_on_a_walled_map(int n, int m)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("wall", 19, 20, 0, 2);                        // east edge of (19,20)
        doc.AddObject("wall", 20, 20, 0, 3);                        // south edge of (20,20)
        doc.AddObject("wall", 23, 21, 0, 0);                        // west edge of (23,21)
        doc.AddObject("tree", 21, 19, 0, 0);                        // Blocked, south of (21,20)
        TileCollisionMap walled = TileMoveSimulatorTests.Bake(doc);
        TileCollisionMap open = OpenMap();
        TileRect target = Target(m);
        IReadOnlyList<TileCoord> set = TileReach.Set(walled, target, 0, n);

        int denied = 0;
        for (int z = WindowMin; z <= WindowMax; z++)
        for (int x = WindowMin; x <= WindowMax; x++)
        {
            var a = new TileCoord(x, z, 0);
            bool inRange = TileReach.Contains(walled, target, 0, a, n);
            Assert.Equal(set.Contains(a), inRange);
            if (TileReach.Contains(open, target, 0, a, n) && !inRange) denied++;
        }
        Assert.True(denied > 0, "the walls deny at least one anchor that reaches on open ground");
    }

    [Fact]
    public void A_wall_denies_one_attacker_tile_while_another_still_reaches()
    {
        var twoByTwo = new TileRect(20, 20, 2, 2);
        var oneByOne = new TileRect(20, 20, 1, 1);
        var westOfTwo = new TileCoord(18, 20, 0);                   // covers (19,20) and (19,21) on the west edge
        var westOfOne = new TileCoord(18, 19, 0);                   // touches the 1x1 only through (19,20)
        Assert.True(TileReach.Contains(OpenMap(), twoByTwo, 0, westOfTwo, 2));
        Assert.True(TileReach.Contains(OpenMap(), oneByOne, 0, westOfOne, 2));

        TileWorldDocument oneWall = TileMoveSimulatorTests.FlatWorld();
        oneWall.AddObject("wall", 19, 20, 0, 2);                    // east edge of (19,20)
        TileCollisionMap oneWallMap = TileMoveSimulatorTests.Bake(oneWall);
        Assert.True(TileReach.Contains(oneWallMap, twoByTwo, 0, westOfTwo, 2));    // (19,21) still reaches
        Assert.False(TileReach.Contains(oneWallMap, oneByOne, 0, westOfOne, 2));   // its only touching tile is walled

        TileWorldDocument twoWalls = TileMoveSimulatorTests.FlatWorld();
        twoWalls.AddObject("wall", 19, 20, 0, 2);
        twoWalls.AddObject("wall", 19, 21, 0, 2);                   // east edge of (19,21)
        Assert.False(TileReach.Contains(TileMoveSimulatorTests.Bake(twoWalls), twoByTwo, 0, westOfTwo, 2));
    }

    [Theory, MemberData(nameof(Pairings))]
    public void FacingToward_answers_the_touching_side(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        TileRect target = Target(m);
        Assert.Equal(TileDirection.E, TileReach.FacingToward(map, target, 0, new TileCoord(20 - n, 20, 0), n));
        Assert.Equal(TileDirection.W, TileReach.FacingToward(map, target, 0, new TileCoord(20 + m, 20, 0), n));
        Assert.Equal(TileDirection.N, TileReach.FacingToward(map, target, 0, new TileCoord(20, 20 - n, 0), n));
        Assert.Equal(TileDirection.S, TileReach.FacingToward(map, target, 0, new TileCoord(20, 20 + m, 0), n));
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void FacingToward_an_empty_footprint_falls_back_to_west(int n)
    {
        // (18,19) at size 2 would read as touching the empty rect's west edge if emptiness were not checked.
        Assert.Equal(TileDirection.W,
            TileReach.FacingToward(OpenMap(), new TileRect(20, 20, 0, 0), 0, new TileCoord(20 - n, 19, 0), n));
    }

    [Theory, MemberData(nameof(Pairings))]
    public void TryNearest_stops_on_an_in_range_anchor_by_the_shortest_walk(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        TileRect target = Target(m);
        var from = new TileCoord(10, 21, 0);

        Assert.True(TileReach.TryNearest(map, target, 0, from, n, 64, out TileCoord reachTile, out TilePath path));
        Assert.True(TileReach.Contains(map, target, 0, reachTile, n));
        long chebyshev = BruteForceAnchors(n, m)
            .Min(a => Math.Max(Math.Abs((long)a.X - from.X), Math.Abs((long)a.Z - from.Z)));
        Assert.Equal(10 - n, chebyshev);                            // the west side, n tiles out from x 20
        Assert.Equal(chebyshev, path.Tiles.Count);
        Assert.Equal(reachTile, path.End);

        var already = new TileCoord(20 - n, 20, 0);
        Assert.True(TileReach.TryNearest(map, target, 0, already, n, 64, out TileCoord stay, out TilePath none));
        Assert.Equal(already, stay);
        Assert.Empty(none.Tiles);
    }

    [Fact]
    public void TryNearest_admits_a_large_agent_whose_nearest_candidate_is_inside_the_window()
    {
        TileCollisionMap map = OpenMap();
        var target = new TileRect(17, 20, 1, 1);
        const int maxRadius = 4;

        // Footprint distance 7 = maxRadius + 3. The west candidate (14, 20) is 4 away.
        Assert.True(TileReach.TryNearest(map, target, 0, new TileCoord(10, 20, 0), 3, maxRadius,
            out TileCoord tile, out TilePath path));
        Assert.Equal(new TileCoord(14, 20, 0), tile);
        Assert.Equal(4, path.Tiles.Count);

        // Agent size 1: distance 5 = maxRadius + 1 resolves, distance 6 does not.
        Assert.True(TileReach.TryNearest(map, target, 0, new TileCoord(12, 20, 0), 1, maxRadius, out _, out _));
        Assert.False(TileReach.TryNearest(map, target, 0, new TileCoord(11, 20, 0), 1, maxRadius, out _, out _));
    }

    [Theory, MemberData(nameof(Pairings))]
    public void An_overlapping_agent_is_never_in_range_and_TryNearest_walks_it_out(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        TileRect target = Target(m);
        var from = new TileCoord(20, 20, 0);

        Assert.False(TileReach.Contains(map, target, 0, from, n));
        Assert.True(TileReach.TryNearest(map, target, 0, from, n, 64, out TileCoord reachTile, out TilePath path));
        Assert.NotEmpty(path.Tiles);
        Assert.Equal(reachTile, path.End);
        Assert.True(new TileRect(path.End.X, path.End.Z, n, n).Intersect(target).IsEmpty);
        Assert.True(TileReach.Contains(map, target, 0, path.End, n));
    }
}
