using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>The stepper's follow, walk and step for an NxN body against an MxM target footprint anchored at (20, 20).
/// Both are anchored on their south-west tile and z counts north. Wall rotation: 0 W, 1 N, 2 E, 3 S edge of the
/// placed tile, mirrored onto the neighbour.</summary>
public class TileFootprintFollowTests
{
    const float Dt = 0.25f;
    const long TargetId = 42L;

    sealed class FakeFootprints : ITileTargets
    {
        public readonly Dictionary<long, TileRect> Rects = new();
        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            plane = 0;
            return Rects.TryGetValue(target, out footprint);
        }
    }

    static (TileMoveSimulator sim, FakeFootprints targets) Sim()
    {
        var targets = new FakeFootprints();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        return (new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks, null, null, targets), targets);
    }

    public static TheoryData<int, int> Pairings()
    {
        var data = new TheoryData<int, int>();
        for (int a = 1; a <= 3; a++) for (int t = 1; t <= 3; t++) data.Add(a, t);
        return data;
    }

    static TileRect Target(int m) => new(20, 20, m, m);

    static TileMoveState At(int x, int z, int size)
    {
        TileMoveState s = TileMoveState.At(new TileCoord(x, z, 0), TileDirection.N);
        s.FootprintSize = size;
        return s;
    }

    static bool InRange(TileMoveSimulator sim, in TileMoveState s, TileRect rect) =>
        TileReach.Contains(sim.Map, rect, 0, s.Tile, s.FootprintSize);

    static bool Standing(TileMoveSimulator sim, in TileMoveState s, TileRect rect) =>
        s.Route.IsIdle && !s.IsStepping && InRange(sim, s, rect);

    // Geometry alone, independent of TileReach: the rects do not overlap and share a cardinal edge.
    static bool TouchesOnOpenGround(TileRect a, TileRect target)
    {
        bool overlap = !a.Intersect(target).IsEmpty;
        bool xTouch = (a.X1 == target.X || target.X1 == a.X) && a.Z < target.Z1 && a.Z1 > target.Z;
        bool zTouch = (a.Z1 == target.Z || target.Z1 == a.Z) && a.X < target.X1 && a.X1 > target.X;
        return !overlap && (xTouch || zTouch);
    }

    // The click, then run held until the body stands in range, for at most 60 ticks. The first tile the body is in
    // range on comes back too, so a caller can pin that the body never moved once it got there.
    static TileMoveState Chase(TileMoveSimulator sim, TileMoveState s, TileRect rect, out TileCoord firstInRange)
    {
        s = sim.Step(s, TileCommand.Attack(TargetId, TileMoveMode.Run), Dt);
        bool seen = InRange(sim, s, rect);
        firstInRange = seen ? s.Tile : default;
        for (int i = 0; i < 60 && !Standing(sim, s, rect); i++)
        {
            s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
            if (!seen && InRange(sim, s, rect))
            {
                seen = true;
                firstInRange = s.Tile;
            }
        }
        Assert.True(seen, "the body never came into range");
        return s;
    }

    static void HoldsFor(TileMoveSimulator sim, TileMoveState s, int ticks)
    {
        TileCoord tile = s.Tile;
        for (int i = 0; i < ticks; i++)
        {
            s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
            Assert.Equal(tile, s.Tile);
            Assert.True(s.Route.IsIdle);
            Assert.Equal(TargetId, s.CombatTarget);
        }
    }

    [Theory, MemberData(nameof(Pairings))]
    public void An_approach_from_open_ground_stops_on_an_in_range_anchor_and_stands(int n, int m)
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileRect rect = Target(m);
        targets.Rects[TargetId] = rect;

        TileMoveState s = Chase(sim, At(10, 21, n), rect, out TileCoord firstInRange);

        Assert.True(s.Route.IsIdle);
        Assert.False(s.IsStepping);
        Assert.True(TileReach.Contains(sim.Map, rect, 0, s.Tile, n));
        Assert.Equal(TargetId, s.CombatTarget);
        Assert.Equal(TileDirection.E, s.Facing);
        Assert.Equal(firstInRange, s.Tile);
        HoldsFor(sim, s, 12);
    }

    // The step out is ONE route and not a walk re-decided every tick: the search runs on the click, and rule 5's memo
    // carries it the rest of the way. Counted on the route's own step list rather than on Route.End, because a
    // re-path that picked the same anchor again would build a new list behind an unchanged end.
    [Theory, MemberData(nameof(Pairings))]
    public void An_overlapping_attacker_steps_out_then_stands_in_one_route(int n, int m)
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileRect rect = Target(m);
        targets.Rects[TargetId] = rect;

        TileMoveState s = sim.Step(At(rect.X, rect.Z, n), TileCommand.Attack(TargetId, TileMoveMode.Run), Dt);
        var routes = new List<IReadOnlyList<TileCoord>>();
        for (int i = 0; i < 60 && !Standing(sim, s, rect); i++)
        {
            if (!s.Route.IsIdle && (routes.Count == 0 || !ReferenceEquals(routes[^1], s.Route.Tiles)))
                routes.Add(s.Route.Tiles);
            s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        }

        Assert.True(routes.Count <= 1, $"the step out was re-pathed {routes.Count} times");
        Assert.Equal(n, s.FootprintSize);
        Assert.True(s.Footprint.Intersect(rect).IsEmpty);
        Assert.True(Standing(sim, s, rect));
        Assert.Equal(TargetId, s.CombatTarget);
        HoldsFor(sim, s, 12);
    }

    // Slides the target one tile along the side it touches, north first. A 1x1 against a 1x1 has no slide that keeps
    // contact, so that one pairing moves round the attacker's corner onto its next side instead.
    [Theory, MemberData(nameof(Pairings))]
    public void A_target_that_moves_within_range_costs_no_re_path(int n, int m)
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileRect rect = Target(m);
        targets.Rects[TargetId] = rect;
        TileMoveState s = Chase(sim, At(10, 21, n), rect, out _);
        Assert.True(Standing(sim, s, rect));

        TileRect moved = default;
        bool found = false;
        foreach ((int dx, int dz) in new[] { (0, 1), (0, -1), (-1, 1), (-1, -1) })
        {
            var candidate = new TileRect(rect.X + dx, rect.Z + dz, m, m);
            if (!TouchesOnOpenGround(s.Footprint, candidate)) continue;
            moved = candidate;
            found = true;
            break;
        }
        Assert.True(found);
        Assert.True(n + m == 2 || moved.X == rect.X, "a slide along the touching side whenever one exists");

        TileCoord tile = s.Tile;
        targets.Rects[TargetId] = moved;
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);

        Assert.True(s.Route.IsIdle);
        Assert.Equal(tile, s.Tile);
        Assert.False(s.IsStepping);
        Assert.Equal(TargetId, s.CombatTarget);
        // Still in range of where the target went, and TURNED to it: a body that answered the old rect would keep
        // the old facing, and one that re-pathed would have a route.
        Assert.True(InRange(sim, s, moved));
        Assert.Equal(TileReach.FacingToward(sim.Map, moved, 0, s.Tile, n), s.Facing);
        HoldsFor(sim, s, 6);
    }

    // Rule 5's memo is the route ITSELF, so a target that slides while the route end is still in range of its
    // footprint costs no search at all. Asserted on the route's step list, which a re-path replaces even when it
    // picks the same anchor again, rather than on Route.End, which it would not move. A 2x2 walking on a 2x2 is the
    // geometry that has a slide at all: the end anchor covers two rows, so the target can step north and still touch
    // it, which a one tile pairing cannot do.
    [Fact]
    public void A_target_that_slides_inside_the_route_ends_range_keeps_the_very_same_route()
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileRect rect = Target(2);
        targets.Rects[TargetId] = rect;

        TileMoveState s = sim.Step(At(10, 21, 2), TileCommand.Attack(TargetId, TileMoveMode.Run), Dt);
        Assert.False(s.Route.IsIdle);
        IReadOnlyList<TileCoord> issued = s.Route.Tiles;
        TileCoord end = s.Route.End;

        var slid = new TileRect(rect.X, rect.Z + 1, 2, 2);
        targets.Rects[TargetId] = slid;
        Assert.True(TileReach.Contains(sim.Map, slid, 0, end, 2), "the slide keeps the route end in range");

        for (int i = 0; i < 20 && !s.Route.IsIdle; i++)
        {
            s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
            if (!s.Route.IsIdle) Assert.Same(issued, s.Route.Tiles);
            Assert.Equal(end, s.Route.IsIdle ? end : s.Route.End);
        }

        for (int i = 0; i < 20 && !Standing(sim, s, slid); i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(end, s.Tile);
        Assert.True(Standing(sim, s, slid));
        Assert.Equal(TargetId, s.CombatTarget);
    }

    // Spec section 15, the other half: the target LEAVES the route end's range, so the follow re-paths, and the body
    // finishes on an anchor in range of where the target went. The same 2x2 geometry as the sibling above, so the
    // only difference between the two is how far the target slid.
    [Fact]
    public void A_target_that_leaves_the_route_ends_range_makes_the_follower_re_path()
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileRect rect = Target(2);
        targets.Rects[TargetId] = rect;

        TileMoveState s = sim.Step(At(10, 21, 2), TileCommand.Attack(TargetId, TileMoveMode.Run), Dt);
        Assert.False(s.Route.IsIdle);
        IReadOnlyList<TileCoord> issued = s.Route.Tiles;
        TileCoord end = s.Route.End;

        var left = new TileRect(rect.X, rect.Z + 3, 2, 2);
        targets.Rects[TargetId] = left;
        Assert.False(TileReach.Contains(sim.Map, left, 0, end, 2), "the target really left the route end's range");

        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);

        Assert.False(s.Route.IsIdle);
        Assert.NotSame(issued, s.Route.Tiles);
        Assert.NotEqual(end, s.Route.End);
        Assert.True(TileReach.Contains(sim.Map, left, 0, s.Route.End, 2), "the new route ends in range of the target");

        for (int i = 0; i < 40 && !Standing(sim, s, left); i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.True(Standing(sim, s, left));
        Assert.NotEqual(end, s.Tile);
        Assert.Equal(TargetId, s.CombatTarget);
    }

    // #741. A footprint that IS the attacker moves with the body, so no tile it could step to is off it, and the lock
    // clears on the tick it is applied rather than holding forever.
    [Fact]
    public void A_self_lock_clears_on_the_tick_it_is_applied()
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileMoveState start = At(10, 10, 2);
        targets.Rects[7L] = start.Footprint;

        TileMoveState s = sim.Step(start, TileCommand.Attack(7L, TileMoveMode.Run), Dt, self: 7L);
        Assert.Equal(0L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(start.Tile, s.Tile);
        Assert.False(s.IsStepping);

        // A lock that reached the state some other way, a direct write, clears on the next tick the same way.
        s.CombatTarget = 7L;
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt, self: 7L);
        Assert.Equal(0L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(start.Tile, s.Tile);
    }

    // #741 MID WALK, which a lock applied while standing cannot see: the route is dropped, not just the lock. The step
    // in flight has already committed its tile, so the body finishes that step and stands on it.
    [Theory, InlineData(1), InlineData(2)]
    public void A_self_lock_applied_mid_walk_drops_the_route_and_stands_on_the_committed_tile(int size)
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileMoveState s = sim.Step(At(10, 10, size), TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Walk),
            Dt, self: 7L);
        for (int i = 0; i < 5; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt, self: 7L);
        Assert.True(s.IsStepping);
        Assert.False(s.Route.IsIdle);
        TileCoord committed = s.Tile;
        Assert.NotEqual(new TileCoord(10, 10, 0), committed);

        targets.Rects[7L] = s.Footprint;
        s = sim.Step(s, TileCommand.Attack(7L, TileMoveMode.Walk), Dt, self: 7L);
        Assert.Equal(0L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(committed, s.Tile);

        for (int i = 0; i < 12; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt, self: 7L);
        Assert.Equal(committed, s.Tile);
        Assert.False(s.IsStepping);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(0L, s.CombatTarget);
    }

    // #920. The self clear is asked BEFORE the target seam is, so it does not depend on the seam resolving the local
    // id. Nothing is put in Rects here, which is the client shape the coupling mattered for: through rule 2 ("the
    // target stopped resolving") the lock clears but the ROUTE survives, so the walk carries on and the head that
    // could not resolve its own id predicts a walk the server stopped.
    [Theory, InlineData(1), InlineData(2)]
    public void A_self_lock_clears_with_the_route_dropped_even_when_the_seam_cannot_resolve_the_id(int size)
    {
        (TileMoveSimulator sim, FakeFootprints targets) = Sim();
        TileMoveState s = sim.Step(At(10, 10, size), TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Walk),
            Dt, self: 7L);
        for (int i = 0; i < 5; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt, self: 7L);
        Assert.False(s.Route.IsIdle);
        TileCoord committed = s.Tile;

        Assert.False(targets.Rects.ContainsKey(7L), "the seam cannot resolve the local id");
        s = sim.Step(s, TileCommand.Attack(7L, TileMoveMode.Walk), Dt, self: 7L);

        Assert.Equal(0L, s.CombatTarget);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(committed, s.Tile);

        for (int i = 0; i < 12; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt, self: 7L);
        Assert.Equal(committed, s.Tile);
        Assert.False(s.IsStepping);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(0L, s.CombatTarget);
    }

    // One wall line along the north edge of row z 15, right across the region, with a single tile left out at x 20.
    // That gap is a doorway exactly one tile wide, which is the sharpest reading of the stepped size there is: the
    // simulator carries no size of its own any more, so a 1x1 state walks through it and a 2x2 state cannot, and no
    // option on the simulator can move either answer.
    [Fact]
    public void A_state_is_stepped_at_exactly_its_own_footprint_size()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        for (int x = 0; x < TileRegion.Size; x++)
            if (x != 20) doc.AddObject("wall", x, 15, 0, 1);      // north edge of (x,15), south edge of (x,16)
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), TileMoveSimulatorTests.Ticks);
        var goal = new TileCoord(20, 18, 0);

        Assert.Equal(new TileRect(5, 6, 1, 1), sim.FootprintOf(At(5, 6, 1)));
        Assert.Equal(new TileRect(5, 6, 2, 2), sim.FootprintOf(At(5, 6, 2)));
        Assert.Equal(new TileRect(5, 6, 3, 3), sim.FootprintOf(At(5, 6, 3)));

        TileMoveState Walk(int size)
        {
            TileMoveState s = sim.Step(At(20, 12, size), TileCommand.WalkTo(goal, TileMoveMode.Run), Dt);
            if (size > 1) Assert.True(s.Route.IsIdle || !s.Route.End.Equals(goal), "the route ends short");
            for (int i = 0; i < 60 && (!s.Route.IsIdle || s.IsStepping); i++)
                s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
            Assert.True(s.Route.IsIdle);
            Assert.False(s.IsStepping);
            return s;
        }

        Assert.Equal(goal, Walk(1).Tile);
        TileMoveState wide = Walk(2);
        Assert.NotEqual(goal, wide.Tile);
        Assert.True(wide.Tile.Z <= 14, $"a 2x2 body crossed the doorway and stands on {wide.Tile}");
    }

    // Two parallel wall lines, on the west and east edges of column x 20, from row 10 to the region's north edge. The
    // column between them is a one tile corridor whose only mouth is its south end, and the goal is inside it.
    [Fact]
    public void A_large_walker_cannot_enter_a_corridor_a_one_tile_walker_walks()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        for (int z = 10; z < TileRegion.Size; z++)
        {
            doc.AddObject("wall", 19, z, 0, 2);                      // east edge of (19,z), west edge of (20,z)
            doc.AddObject("wall", 21, z, 0, 0);                      // west edge of (21,z), east edge of (20,z)
        }
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), TileMoveSimulatorTests.Ticks);
        var goal = new TileCoord(20, 16, 0);
        // The walled run itself: column 20 from the first walled row to the region's north edge. A 2x2 standing on
        // any of it straddles one of the two wall lines, so the strong statement is that its footprint never covers a
        // tile of this rect on any tick, not merely that it stopped somewhere other than the goal.
        var corridor = new TileRect(20, 10, 1, TileRegion.Size - 10);

        TileMoveState Walk(int size)
        {
            TileMoveState s = sim.Step(At(20, 4, size), TileCommand.WalkTo(goal, TileMoveMode.Run), Dt);
            if (size > 1) Assert.True(s.Route.IsIdle || !s.Route.End.Equals(goal), "the route ends short");
            for (int i = 0; i < 60 && (!s.Route.IsIdle || s.IsStepping); i++)
            {
                s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
                if (size > 1)
                    Assert.True(s.Footprint.Intersect(corridor).IsEmpty, $"the 2x2 stood in the corridor on {s.Tile}");
            }
            Assert.True(s.Route.IsIdle);
            Assert.False(s.IsStepping);
            return s;
        }

        Assert.Equal(goal, Walk(1).Tile);

        TileMoveState large = Walk(2);
        Assert.NotEqual(goal, large.Tile);
        Assert.True(large.Footprint.Intersect(corridor).IsEmpty, $"the 2x2 finished in the corridor on {large.Tile}");
        Assert.True(TileCollision.CanStand(sim.Map, large.Tile.X, large.Tile.Z, 0, 2));
    }
}
