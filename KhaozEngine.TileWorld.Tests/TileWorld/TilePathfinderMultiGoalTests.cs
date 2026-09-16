using System.Collections.Generic;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The multi-goal entry point, pinned against the single-goal one it shares its expansion with:
/// <see cref="TilePathfinder.FindPathToAny"/> must answer what a <see cref="TilePathfinder.FindPath"/> per goal
/// answers, step for step, including the tie rule.</summary>
public class TilePathfinderMultiGoalTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileCollisionMap OpenMap() => TileCollisionBaker.Bake(TileWorldTestData.FlatWorld(), Cat);

    // A wall line at x = 10 with a gap at z 20 and above, plus a sealed box around (31, 31), so one battery covers
    // the open walk, the detour and the goal nobody can reach.
    static TileCollisionMap ObstacleMap()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < 20; z++) doc.AddObject("wall", 10, z, 0, 2);
        for (int i = 29; i <= 33; i++) { doc.AddObject("tree", i, 29, 0, 0); doc.AddObject("tree", i, 33, 0, 0); }
        for (int i = 30; i <= 32; i++) { doc.AddObject("tree", 29, i, 0, 0); doc.AddObject("tree", 33, i, 0, 0); }
        return TileCollisionBaker.Bake(doc, Cat);
    }

    static List<TileCoord> Goals(params (int X, int Z)[] tiles)
    {
        var goals = new List<TileCoord>();
        foreach ((int x, int z) in tiles) goals.Add(new TileCoord(x, z, 0));
        return goals;
    }

    // The definition the one search has to match: a search per goal, shortest walk wins, first of a tie kept.
    static int Reference(TileCollisionMap map, TileCoord start, IReadOnlyList<TileCoord> goals, int agentSize,
        int maxRadius, out TilePath path)
    {
        path = new TilePath(System.Array.Empty<TileCoord>(), reached: false, start);
        int best = int.MaxValue, won = -1;
        for (int i = 0; i < goals.Count; i++)
        {
            if (goals[i].X == start.X && goals[i].Z == start.Z) return i;   // nothing beats zero steps
            TilePath p = TilePathfinder.FindPath(map, 0, start, goals[i], agentSize, maxRadius);
            if (!p.Reached || p.Tiles.Count >= best) continue;
            best = p.Tiles.Count;
            won = i;
            path = p;
        }
        return won;
    }

    static void AssertMatchesReference(TileCollisionMap map, TileCoord start, IReadOnlyList<TileCoord> goals,
        int agentSize, int maxRadius)
    {
        int expectedIndex = Reference(map, start, goals, agentSize, maxRadius, out TilePath expected);
        TilePath actual = TilePathfinder.FindPathToAny(map, 0, start, goals, agentSize, maxRadius, null,
            out int index);

        Assert.Equal(expectedIndex, index);
        if (expectedIndex < 0)
        {
            Assert.False(actual.Reached);
            Assert.Empty(actual.Tiles);
            return;
        }
        if (goals[expectedIndex].X == start.X && goals[expectedIndex].Z == start.Z)
        {
            Assert.True(actual.Reached);
            Assert.Empty(actual.Tiles);
            return;
        }
        Assert.True(actual.Reached);
        Assert.Equal(expected.Tiles, actual.Tiles);
        Assert.Equal(expected.End, actual.End);
    }

    [Fact]
    public void One_search_walks_exactly_what_a_search_per_goal_walks()
    {
        TileCollisionMap open = OpenMap(), obstacles = ObstacleMap();

        AssertMatchesReference(open, new TileCoord(5, 5, 0), Goals((9, 5), (5, 9), (12, 12), (2, 2)), 1, 64);
        AssertMatchesReference(open, new TileCoord(5, 5, 0), Goals((20, 20), (5, 6)), 1, 64);
        AssertMatchesReference(obstacles, new TileCoord(8, 5, 0), Goals((12, 5), (12, 6), (8, 12)), 1, 64);
        AssertMatchesReference(obstacles, new TileCoord(20, 20, 0), Goals((31, 31), (24, 26), (18, 18)), 2, 12);
        AssertMatchesReference(obstacles, new TileCoord(5, 31, 0), Goals((31, 31), (30, 31)), 1, 64);   // sealed box
        AssertMatchesReference(open, new TileCoord(5, 5, 0), Goals((5, 5), (6, 5)), 1, 8);              // on a goal
    }

    // The level rule, which is the whole reason the search finishes the level it found a goal on. Two goals the
    // same walk away must resolve by INDEX, so swapping them swaps the answer. Whichever of the two the flood
    // discovers first, one of these two assertions contradicts "the first goal discovered wins".
    [Fact]
    public void Two_goals_the_same_walk_away_resolve_by_their_index()
    {
        TileCollisionMap map = OpenMap();
        var start = new TileCoord(5, 5, 0);
        var east = new TileCoord(9, 5, 0);
        var north = new TileCoord(5, 9, 0);
        Assert.Equal(4, TilePathfinder.FindPath(map, 0, start, east).Tiles.Count);
        Assert.Equal(4, TilePathfinder.FindPath(map, 0, start, north).Tiles.Count);

        TilePath first = TilePathfinder.FindPathToAny(map, 0, start, new[] { east, north }, 1, 64, null, out int a);
        TilePath second = TilePathfinder.FindPathToAny(map, 0, start, new[] { north, east }, 1, 64, null, out int b);
        Assert.Equal(0, a);
        Assert.Equal(0, b);
        Assert.Equal(east, first.End);
        Assert.Equal(north, second.End);
        Assert.Equal(TilePathfinder.FindPath(map, 0, start, east).Tiles, first.Tiles);
        Assert.Equal(TilePathfinder.FindPath(map, 0, start, north).Tiles, second.Tiles);
    }

    [Fact]
    public void A_shorter_goal_beats_an_earlier_one_whatever_the_order()
    {
        TileCollisionMap map = OpenMap();
        var start = new TileCoord(5, 5, 0);
        TilePath p = TilePathfinder.FindPathToAny(map, 0, start, Goals((20, 20), (6, 6), (30, 5)), 1, 64, null,
            out int index);
        Assert.Equal(1, index);
        Assert.Single(p.Tiles);
        Assert.Equal(new TileCoord(6, 6, 0), p.End);
    }

    [Fact]
    public void A_goal_the_window_cannot_hold_is_never_chosen()
    {
        TileCollisionMap map = OpenMap();
        var start = new TileCoord(20, 20, 0);
        // (24, 20) is inside a radius 4 window, (26, 20) is not, so the far one loses however it is ordered.
        TilePath p = TilePathfinder.FindPathToAny(map, 0, start, Goals((26, 20), (24, 20)), 1, 4, null, out int i);
        Assert.Equal(1, i);
        Assert.Equal(4, p.Tiles.Count);

        Assert.False(TilePathfinder.FindPathToAny(map, 0, start, Goals((26, 20)), 1, 4, null, out int none).Reached);
        Assert.Equal(-1, none);
    }

    [Fact]
    public void A_goal_on_the_start_tile_is_a_zero_step_reached_path()
    {
        TilePath p = TilePathfinder.FindPathToAny(OpenMap(), 0, new TileCoord(5, 5, 0), Goals((6, 5), (5, 5)), 1, 64,
            null, out int index);
        Assert.Equal(1, index);
        Assert.True(p.Reached);
        Assert.Empty(p.Tiles);
        Assert.Equal(new TileCoord(5, 5, 0), p.End);
    }

    // No nearest-reachable fallback, which is the one place this differs from FindPath: a goal SET has no single
    // tile to measure nearness to, so an unreachable set answers nothing rather than a walk that stops short.
    [Fact]
    public void An_empty_list_and_an_unreachable_set_both_answer_a_not_reached_empty_path()
    {
        TileCollisionMap map = ObstacleMap();
        var start = new TileCoord(5, 31, 0);

        TilePath empty = TilePathfinder.FindPathToAny(map, 0, start, System.Array.Empty<TileCoord>(), 1, 64, null,
            out int noGoals);
        Assert.Equal(-1, noGoals);
        Assert.False(empty.Reached);
        Assert.Empty(empty.Tiles);
        Assert.Equal(start, empty.End);

        TilePath sealedBox = TilePathfinder.FindPathToAny(map, 0, start, Goals((31, 31), (30, 31), (31, 30)), 1, 64,
            null, out int index);
        Assert.Equal(-1, index);
        Assert.False(sealedBox.Reached);
        Assert.Empty(sealedBox.Tiles);
        Assert.Equal(start, sealedBox.End);
        // FindPath for the same goal DOES walk to the ring outside the box, which is what is being refused here.
        Assert.NotEmpty(TilePathfinder.FindPath(map, 0, start, new TileCoord(31, 31, 0)).Tiles);
    }

    // The point of the whole entry point, counted rather than timed: a goal set nobody can reach floods the window
    // ONCE, not once per goal. The scratch counts cells dequeued and searches run across the call.
    [Fact]
    public void An_unreachable_goal_set_floods_one_window_and_no_more()
    {
        TileCollisionMap map = ObstacleMap();
        var start = new TileCoord(5, 31, 0);
        const int radius = 16;
        var scratch = new TilePathfinderScratch(radius);
        List<TileCoord> goals = Goals((31, 31), (30, 31), (31, 30), (32, 31), (31, 32), (30, 30), (32, 32), (30, 32));

        scratch.ClearCounters();
        Assert.False(TilePathfinder.FindPathToAny(map, 0, start, goals, 1, radius, scratch, out _).Reached);
        int cells = (2 * radius + 1) * (2 * radius + 1);
        Assert.Equal(1, scratch.SearchesRun);
        Assert.True(scratch.CellsExpanded <= cells,
            $"the search dequeued {scratch.CellsExpanded} cells, past the {cells} its window holds");

        // The shape it replaces, measured the same way: one search per goal is eight windows of the same flood.
        scratch.ClearCounters();
        foreach (TileCoord goal in goals) TilePathfinder.FindPath(map, 0, start, goal, 1, radius, scratch);
        Assert.Equal(goals.Count, scratch.SearchesRun);
        Assert.True(scratch.CellsExpanded > 4 * cells,
            $"a search per goal dequeued {scratch.CellsExpanded} cells, expected several windows");
    }
}
