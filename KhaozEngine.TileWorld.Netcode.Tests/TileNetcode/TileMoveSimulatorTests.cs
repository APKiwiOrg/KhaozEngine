using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileMoveSimulatorTests
{
    internal static readonly TileWorldCatalogs Catalogs = TileWorldCatalogs.Greybox();
    internal const float Dt = 0.25f;
    internal static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    internal static TileWorldDocument FlatWorld(int planeCount = 4, params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "test", DisplayName = "Test", PlaneCount = planeCount };
        if (regions.Length == 0) regions = new[] { new RegionCoord(0, 0) };
        foreach (RegionCoord c in regions)
        {
            doc.GetOrCreateRegion(c);
            TileRect rect = c.Rect;
            for (int z = rect.Z; z < rect.Z1; z++)
                for (int x = rect.X; x < rect.X1; x++) doc.SetUnderlay(x, z, 0, 1);
        }
        return doc;
    }

    internal static TileCollisionMap Bake(TileWorldDocument doc) => TileCollisionBaker.Bake(doc, Catalogs);

    static TileMoveSimulator Sim(TileCollisionMap map) => new(map, Ticks);

    // Ticks a command through and then HOLDS its mode, which is what a client does: the run toggle rides on every
    // command, so a plain TileCommand.None after a run walk would drop the player back to a walking cadence.
    static TileMoveState Run(TileMoveSimulator sim, TileMoveState s, TileCommand first, int ticks)
    {
        s = sim.Step(s, first, Dt);
        for (int i = 1; i < ticks; i++) s = sim.Step(s, TileCommand.Continue(first.Mode), Dt);
        return s;
    }

    [Fact]
    public void Two_instances_on_the_same_inputs_stay_byte_identical()
    {
        TileWorldDocument doc = FlatWorld();
        TileMoveSimulator a = Sim(Bake(doc)), b = Sim(Bake(doc));
        TileMoveState sa = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N);
        TileMoveState sb = sa;
        for (int i = 0; i < 40; i++)
        {
            // Run, drop to a walk mid step, then pick run back up mid step. The mode rides on every command, so a
            // toggle is part of the input stream a replay has to reproduce, not a property of the first click.
            TileCommand c = i switch
            {
                0 => TileCommand.WalkTo(new TileCoord(11, 9, 0), TileMoveMode.Run),
                < 4 => TileCommand.Continue(TileMoveMode.Run),
                < 16 => TileCommand.Continue(TileMoveMode.Walk),
                _ => TileCommand.Continue(TileMoveMode.Run),
            };
            sa = a.Step(sa, c, Dt);
            sb = b.Step(sb, c, Dt);
            Assert.Equal(sa, sb);
        }
        Assert.Equal(new TileCoord(11, 9, 0), sa.Tile);
    }

    // THE lead-commit pin. A step commits its tile on the tick it STARTS, not on the tick the body arrives, so the
    // simulation owns the tile the avatar is walking into for the whole of the walk into it. The drawn position is
    // the control: it is a quarter of the way along the step on the first tick under either rule, so the only thing
    // this test moves is WHICH TILE the state names while that quarter is drawn.
    [Fact]
    public void A_step_commits_its_tile_when_it_starts_and_the_body_glides_in_after()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.S);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Walk), Dt);

        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);           // committed at the START of the step
        Assert.Equal(new TileCoord(0, 0, 0), s.StepFrom);       // and the body is still leaving this one
        Assert.Equal(new Vector2(0f, 0.25f), s.Position);       // one tick of a four tick step, drawn as it always was
        Assert.Equal(TileDirection.N, s.Facing);

        // Three more ticks of glide land the body on the tile the simulation already owned, without committing a
        // second one: the cadence is unchanged, only the moment the tile changes moved.
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);           // the NEXT step commits as the body lands
        Assert.Equal(new TileCoord(0, 1, 0), s.StepFrom);
        Assert.Equal(new Vector2(0f, 1f), s.Position);          // drawn exactly on the tile it just walked into
    }

    // The CADENCE is unchanged by the lead commit: a body still crosses a tile every two ticks running and every
    // four walking. What moved is when the tile changes, so the body is measured through Position and the commit
    // through Tile, one tile apart for the whole of every step.
    [Fact]
    public void A_run_crosses_a_tile_every_two_ticks_and_a_walk_every_four()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Run), 1);
        Assert.Equal(new Vector2(0f, 0.5f), s.Position);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        // The body landed on (0, 1) and the step into (0, 2) was committed on the same tick, which is why progress
        // reads zero rather than the total.
        Assert.Equal(new Vector2(0f, 1f), s.Position);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
        Assert.Equal(0, s.StepTicks);

        TileMoveState w = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        w = Run(sim, w, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Walk), 3);
        Assert.Equal(new Vector2(0f, 0.75f), w.Position);
        Assert.Equal(new TileCoord(0, 1, 0), w.Tile);
        w = sim.Step(w, TileCommand.None, Dt);
        Assert.Equal(new Vector2(0f, 1f), w.Position);
        Assert.Equal(new TileCoord(0, 2, 0), w.Tile);
    }

    [Fact]
    public void A_diagonal_step_costs_the_same_as_a_cardinal_one_and_sets_the_facing()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(3, 3, 0), TileMoveMode.Run), 2);
        Assert.Equal(new Vector2(1f, 1f), s.Position);       // one whole diagonal step, in two ticks
        Assert.Equal(new TileCoord(2, 2, 0), s.Tile);        // with the next one already committed
        Assert.Equal(TileDirection.NE, s.Facing);
    }

    [Fact]
    public void A_corner_is_never_cut()
    {
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("tree", 1, 0, 0, 0);
        doc.AddObject("tree", 0, 1, 0, 0);
        TileMoveSimulator sim = Sim(Bake(doc));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(1, 1, 0), TileMoveMode.Run), 12);
        Assert.NotEqual(new TileCoord(1, 1, 0), s.Tile);
        Assert.Equal(new TileCoord(0, 0, 0), s.Tile);
    }

    [Fact]
    public void An_unreachable_goal_walks_to_the_nearest_reachable_tile()
    {
        TileWorldDocument doc = FlatWorld();
        // The column has to span the WHOLE region. A partial wall leaves a way round its end, and the
        // pathfinder finds it, so the goal is reachable and nothing here is testing the fallback.
        for (int z = 0; z < TileRegion.Size; z++) doc.AddObject("tree", 5, z, 0, 0);
        TileMoveSimulator sim = Sim(Bake(doc));
        TileMoveState s = TileMoveState.At(new TileCoord(2, 5, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(8, 5, 0), TileMoveMode.Run), 60);
        Assert.True(s.Route.IsIdle);
        // The tile the rule PICKS, not just any tile short of the wall: reachable tiles are x <= 4, and (4, 5) is
        // the unique nearest to the goal (8, 5) at a squared distance of 16, against 17 for (4, 4) and (4, 6).
        // Asserting "west of the wall" instead passes for a simulator that never moves off (2, 5) at all.
        Assert.Equal(new TileCoord(4, 5, 0), s.Tile);
    }

    // The re-path ATTEMPT is pinned by the sibling below, A_blocker_with_a_way_round_re_paths_and_still_arrives,
    // which fails outright without one. This test cannot tell "re-pathed and found nothing" from "never re-pathed",
    // because the re-path returns an empty path in this setup either way, so do not delete the sibling believing
    // this one covers it.
    [Fact]
    public void A_blocker_that_appears_mid_route_re_paths_once_and_then_stands()
    {
        TileWorldDocument doc = FlatWorld();
        TileCollisionMap map = Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), 4);
        Assert.Equal(new TileCoord(0, 3, 0), s.Tile);

        // Wall the row AHEAD of the committed tile off, then keep ticking: the step into it never starts, the
        // re-path fails and the route is dropped. The row spans the region because the world either side of it is
        // open, so a few tiles would only make the walk longer. Row 4 rather than row 3 because the step into (0, 3)
        // was committed the tick before this rebake, and a commit is not rewound (see the sibling below).
        for (int x = 0; x < TileRegion.Size; x++) doc.AddObject("tree", x, 4, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(0, 3, TileRegion.Size, 3), 0);
        for (int i = 0; i < 6; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(0, 3, 0), s.Tile);
        Assert.False(s.IsStepping);                          // and the body caught up with it
    }

    // The other side of committing at the START, and a deliberate consequence rather than a hole: a blocker that
    // lands on a tile the player is ALREADY walking into does not rewind that step. The map was asked before the
    // tile flipped, the answer was yes, and the player owns it. The next step is where the new blocker is felt,
    // which is the same tick a standing player would have felt it.
    [Fact]
    public void A_blocker_landing_on_the_tile_a_step_already_committed_to_does_not_rewind_it()
    {
        TileWorldDocument doc = FlatWorld();
        TileCollisionMap map = Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);        // committed, with three ticks of glide left

        doc.AddObject("tree", 0, 1, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(0, 0, 3, 3), 0);
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);

        Assert.Equal(new Vector2(0f, 1f), s.Position);       // the body finished the step it was taking
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);        // and the NEXT step, off the blocked tile, still runs
    }

    [Fact]
    public void A_blocker_with_a_way_round_re_paths_and_still_arrives()
    {
        TileWorldDocument doc = FlatWorld();
        TileCollisionMap map = Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), 4);
        doc.AddObject("tree", 0, 3, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(-1, 2, 3, 3), 0);
        for (int i = 0; i < 30; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 6, 0), s.Tile);
    }

    // Four RUN ticks lands exactly on the tick a step ends, and there is no step BOUNDARY state any more: the tick
    // the body arrives is the tick the next step commits, so a click there still finds a step in flight, at zero
    // progress. The click therefore re-paths from the committed tile and leaves the cadence of the step under way
    // alone, which is the mode rule in its hardest case.
    [Fact]
    public void A_second_walk_command_re_paths_from_the_tile_the_player_is_committed_to()
    {
        TileCollisionMap map = Bake(FlatWorld());
        TileMoveSimulator sim = Sim(map);
        var committed = new TileCoord(0, 3, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 8, 0), TileMoveMode.Run), 4);
        Assert.Equal(committed, s.Tile);
        Assert.Equal(0, s.StepTicks);
        Assert.True(s.IsStepping);

        var goal = new TileCoord(4, 2, 0);
        s = sim.Step(s, TileCommand.WalkTo(goal, TileMoveMode.Walk), Dt);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        // Two, not four: the step into (0, 3) was committed at a run cadence and keeps it. The walk cadence lands
        // when the next step starts.
        Assert.Equal(2, s.StepTotal);
        Assert.Equal(1, s.StepTicks);
        Assert.Equal(goal, s.Route.End);
        // Pathed from the committed tile, compared against a fresh search rather than against literals so the
        // pathfinder's own tie-break stays its business.
        TilePath after = TilePathfinder.FindPath(map, 0, committed, goal, 1, sim.MaxPathRadius);
        Assert.Equal(after.Tiles[0], s.Route.Next);
    }

    // Walks two ticks into a four tick step heading north out of (5, 5), which leaves the avatar drawn half way
    // between (5, 5) and (5, 6) with the step still in flight. Every mid-step test below starts here.
    static TileMoveState HalfWayNorth(TileMoveSimulator sim, TileCoord start)
    {
        TileMoveState s = TileMoveState.At(start, TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(start.X, start.Z + 4, 0), TileMoveMode.Walk), Dt);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        return s;
    }

    // The re-click stutter, and the whole shape of the fix. A WalkTo arriving MID-STEP used to re-path from the
    // tile being LEFT and reset step progress, which drags the presented position back toward the departed tile
    // before setting off again: a visible hitch on every direction change while moving, and client-predicted, so
    // both heads produce it identically and no correction ever cleans it up. The step in progress is never
    // abandoned now, and it needs no splice to survive: the tile a route is pathed from IS the tile the step in
    // flight is walking into.
    //
    // Progress is measured as a dot product ALONG the step's own direction rather than as a raw coordinate,
    // because that is the quantity the presenter glides on and the one a yank makes run backwards. The lateral
    // component is pinned too: the old semantic moved the avatar sideways onto the new route's first step in the
    // same tick, which no amount of forward progress would have caught.
    [Fact]
    public void A_re_click_mid_step_never_walks_the_avatar_back_toward_the_tile_it_left()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        var start = new TileCoord(5, 5, 0);
        var origin = new Vector2(start.X, start.Z);
        var along = new Vector2(0f, 1f);                 // the step in flight is north, into (5, 6)

        TileMoveState s = HalfWayNorth(sim, start);
        Assert.Equal(new TileCoord(5, 6, 0), s.Tile);
        Assert.Equal(start, s.StepFrom);
        Assert.Equal(2, s.StepTicks);
        float progress = Vector2.Dot(s.Position - origin, along);
        Assert.Equal(0.5f, progress, 5);

        // The re-click, due east, then the two remaining ticks of the step it landed in.
        var drawn = new List<Vector2>();
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(9, 5, 0), TileMoveMode.Walk), Dt);
        drawn.Add(s.Position);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        drawn.Add(s.Position);

        foreach (Vector2 p in drawn)
        {
            float now = Vector2.Dot(p - origin, along);
            Assert.True(now >= progress, $"drawn at {p}, which is {progress - now} of a tile back down the step");
            progress = now;
            Assert.Equal(origin.X, p.X, 5);              // and never sideways off the step under way
        }

        // The step ran to its end exactly as it would have without the click, the body landed on the tile it was
        // already committed to, and the new route carries on from there.
        Assert.Equal(1f, progress, 5);
        Assert.Equal(new TileCoord(5, 6, 0), s.StepFrom);
        Assert.Equal(new TileCoord(9, 5, 0), s.Route.End);
    }

    [Fact]
    public void A_re_clicked_walk_still_lands_on_the_tile_the_second_click_named()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = HalfWayNorth(sim, new TileCoord(5, 5, 0));

        // The step in flight is untouched: same destination tile, same cadence, same progress. Only the route
        // behind it changed, and it was pathed from the tile the step is walking into.
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(9, 5, 0), TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(5, 6, 0), s.Tile);
        Assert.Equal(new TileCoord(5, 5, 0), s.StepFrom);
        Assert.Equal(TileMoveMode.Run, s.Mode);
        Assert.Equal(4, s.StepTotal);
        Assert.Equal(3, s.StepTicks);

        for (int i = 0; i < 40 && !s.Route.IsIdle; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        // The route empties as the LAST step commits, so the walk's destination is owned here while the body still
        // has that step to walk. One more tick's worth of glide and it is standing on it.
        Assert.Equal(new TileCoord(9, 5, 0), s.Tile);
        Assert.True(s.Route.IsIdle);
        Assert.True(s.IsStepping);
        for (int i = 0; i < 8; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.False(s.IsStepping);
        Assert.Equal(new Vector2(9f, 5f), s.Position);
    }

    [Fact]
    public void Two_instances_replaying_a_mid_step_re_click_stay_byte_identical()
    {
        TileWorldDocument doc = FlatWorld();
        TileMoveSimulator a = Sim(Bake(doc)), b = Sim(Bake(doc));
        TileMoveState sa = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N);
        TileMoveState sb = sa;
        for (int i = 0; i < 40; i++)
        {
            // Two clicks, the second landing two ticks into a four tick step, plus a run toggle straddling it.
            // The re-click is a function of the state and the command alone, so a replay of the same stream from
            // the same seed reproduces it, which is what prediction reconciles against rather than snapping.
            TileCommand c = i switch
            {
                0 => TileCommand.WalkTo(new TileCoord(5, 9, 0), TileMoveMode.Walk),
                2 => TileCommand.WalkTo(new TileCoord(9, 5, 0), TileMoveMode.Walk),
                < 9 => TileCommand.Continue(TileMoveMode.Walk),
                _ => TileCommand.Continue(TileMoveMode.Run),
            };
            sa = a.Step(sa, c, Dt);
            sb = b.Step(sb, c, Dt);
            Assert.Equal(sa, sb);
        }
        Assert.Equal(new TileCoord(9, 5, 0), sa.Tile);
    }

    // The reconcile replay, at the simulator level and for the case that moved. A client rebases on the newest
    // authoritative state it has and replays every command it has sent since, so the tick that basis was taken on
    // is a property of the NETWORK rather than of the walk: a snapshot arriving a tick early or a tick late has to
    // replay onto the same state, or a healthy walk snaps for no reason. Committing at the START of a step moves
    // which tick each commit lands on, so this is re-proved rather than assumed. Three bases, N-1, N and N+1,
    // replayed to the same end and compared against the straight-line run byte for byte.
    [Fact]
    public void A_replay_from_a_basis_taken_one_tick_either_side_lands_on_the_same_state()
    {
        TileCollisionMap map = Bake(FlatWorld());
        // A stream with the awkward parts in it: a click, a re-click landing mid step, and a run toggle straddling
        // a step boundary, which is exactly the window the two heads can disagree in.
        static TileCommand At(int tick) => tick switch
        {
            0 => TileCommand.WalkTo(new TileCoord(5, 12, 0), TileMoveMode.Walk),
            6 => TileCommand.WalkTo(new TileCoord(24, 5, 0), TileMoveMode.Walk),
            < 11 => TileCommand.Continue(TileMoveMode.Walk),
            _ => TileCommand.Continue(TileMoveMode.Run),
        };
        const int End = 24;

        var straight = new TileMoveSimulator(map, Ticks);
        var basis = new TileMoveState[End + 1];
        TileMoveState s = TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N);
        basis[0] = s;
        for (int tick = 0; tick < End; tick++)
        {
            s = straight.Step(s, At(tick), Dt);
            basis[tick + 1] = s;
        }
        Assert.True(s.IsStepping);                           // the run ends mid step, so the glide is in the compare

        foreach (int taken in new[] { 9, 10, 11 })
        {
            var replay = new TileMoveSimulator(map, Ticks);
            TileMoveState r = basis[taken];
            for (int tick = taken; tick < End; tick++) r = replay.Step(r, At(tick), Dt);
            Assert.Equal(s, r);
            Assert.Equal(s.StepFrom, r.StepFrom);            // compared explicitly, not only through Equals
            Assert.Equal(s.Position, r.Position);
        }
    }

    [Fact]
    public void The_route_cap_counts_the_steps_still_to_take_from_the_committed_tile()
    {
        // The cap is spent on the walk AHEAD, and the step in flight is not part of it: that step was charged
        // against the cap when it was routed, and committed when it started. So a re-click never ratchets a route
        // past the limit the way a spliced one could, it just spends a fresh cap from one tile further on, which
        // is the same thing a player who waited a step and clicked again would get.
        TileCollisionMap map = Bake(FlatWorld());
        var sim = new TileMoveSimulator(map, Ticks, null, new TileMoveOptions { MaxRouteSteps = 3 });
        var goal = new TileCoord(20, 0, 0);
        var entering = new TileCoord(0, 1, 0);
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 20, 0), TileMoveMode.Walk), Dt);
        // Three steps were routed, the first is committed and gliding, so two are still to take.
        Assert.Equal(entering, s.Tile);
        Assert.Equal(2, s.Route.Remaining);

        s = sim.Step(s, TileCommand.WalkTo(goal, TileMoveMode.Walk), Dt);
        Assert.Equal(3, s.Route.Remaining);
        // The three tiles are the pathfinder's own first three FROM the committed tile, compared against a fresh
        // search rather than against literals so the BFS tie-break stays its business.
        TilePath after = TilePathfinder.FindPath(map, 0, entering, goal, 1, sim.MaxPathRadius);
        Assert.Equal(after.Tiles[0], s.Route.Next);
        Assert.Equal(after.Tiles[2], s.Route.End);
        Assert.NotEqual(goal, s.Route.End);
    }

    [Fact]
    public void A_cross_plane_goal_arriving_mid_step_is_still_dropped_whole()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = HalfWayNorth(sim, new TileCoord(5, 5, 0));

        TileMoveState dropped = sim.Step(s, TileCommand.WalkTo(new TileCoord(9, 5, 1), TileMoveMode.Run), Dt);

        // Indistinguishable from a tick that carried no command, mode included, which is the same answer the
        // standing case gives. The splice never becomes a way for a refused click to change the route.
        Assert.Equal(sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt), dropped);
        Assert.Equal(new TileCoord(5, 9, 0), dropped.Route.End);
        Assert.Equal(TileMoveMode.Walk, dropped.Mode);
    }

    [Fact]
    public void A_goal_the_player_already_stands_on_just_stands()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(4, 4, 0), TileDirection.S);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(4, 4, 0), TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(4, 4, 0), s.Tile);
        Assert.Equal(TileDirection.S, s.Facing);
    }

    // A serpentine corridor inside ONE 64x64 region: every odd row is walled off except a single gap tile at
    // alternating ends. A walk from one corner to the other is over two thousand steps with every tile of it inside
    // the pathfinder's DEFAULT search radius, which is the point: the cap is reached on an ordinary click, not on
    // some pathological one.
    internal static TileCollisionMap Serpentine()
    {
        TileCollisionMap map = Bake(FlatWorld(planeCount: 1));
        for (int z = 1; z < TileRegion.Size; z += 2)
        {
            int gap = z / 2 % 2 == 0 ? TileRegion.Size - 1 : 0;
            for (int x = 0; x < TileRegion.Size; x++)
                if (x != gap) map.Or(x, z, 0, TileCollisionFlags.Blocked);
        }
        return map;
    }

    [Fact]
    public void A_route_over_the_cap_is_truncated_to_the_same_tiles_on_both_heads()
    {
        // The cap lives HERE rather than at the encoder because both heads have to walk the same route. Applied on
        // the wire instead, the server would keep walking the full path while the owner's basis named the tile the
        // truncation happened to end on, a destination nobody routed it to, refreshed with a new wrong one every
        // snapshot. Truncated in the simulator, the walk simply ends there, on both heads, byte for byte.
        TileCollisionMap map = Serpentine();
        var start = new TileCoord(0, 0, 0);
        var goal = new TileCoord(0, 62, 0);
        TilePath full = TilePathfinder.FindPath(map, 0, start, goal);
        Assert.True(full.Tiles.Count > TileProtocol.MaxRouteSteps * 4, $"path was {full.Tiles.Count} steps");

        TileMoveSimulator a = Sim(map), b = Sim(map);
        TileCommand click = TileCommand.WalkTo(goal, TileMoveMode.Run);
        TileMoveState sa = a.Step(TileMoveState.At(start, TileDirection.N), click, Dt);
        TileMoveState sb = b.Step(TileMoveState.At(start, TileDirection.N), click, Dt);

        Assert.Equal(TileProtocol.MaxRouteSteps, sa.Route.Tiles.Count);
        Assert.Equal(sa, sb);
        Assert.Equal(sa.Route.End, sb.Route.End);
        // The truncation is the pathfinder's own first MaxRouteSteps tiles, so it needs no second deterministic
        // decision of its own, and the destination is the tile the walk really ends on rather than the click.
        Assert.Equal(full.Tiles[TileProtocol.MaxRouteSteps - 1], sa.Route.End);
        Assert.NotEqual(goal, sa.Route.End);
    }

    [Fact]
    public void The_truncated_route_is_where_the_walk_actually_ends()
    {
        // Run with a tiny cap so the whole walk fits in a test. A click past the cap carries the player as far as
        // one click allows and stops, which is the same answer a click past the search radius already gives.
        var sim = new TileMoveSimulator(Bake(FlatWorld()), Ticks, null, new TileMoveOptions { MaxRouteSteps = 3 });
        Assert.Equal(3, sim.MaxRouteSteps);

        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 20, 0), TileMoveMode.Run), 40);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(0, 3, 0), s.Tile);
    }

    [Fact]
    public void An_interact_route_is_truncated_by_the_same_cap()
    {
        // The interact path builds its route through the same helper, so a booth further away than the cap is
        // walked toward and no further. The pending target rides the walk and is DROPPED when it ends short of the
        // reach set, which is what makes the server answer the click with a CannotReach instead of raising the
        // interaction from wherever the truncated route ran out.
        TileWorldDocument doc = FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var sim = new TileMoveSimulator(Bake(doc), Ticks, new TileDocumentTargets(doc, Catalogs),
            new TileMoveOptions { MaxRouteSteps = 3 });

        TileMoveState s = sim.Step(TileMoveState.At(new TileCoord(0, 10, 0), TileDirection.N),
            TileCommand.Interact(booth.Id, TileMoveMode.Run), Dt);
        Assert.Equal(3, s.Route.Tiles.Count);
        Assert.Equal(new TileCoord(3, 10, 0), s.Route.End);
        Assert.Equal(booth.Id, s.InteractTarget);

        s = Run(sim, s, TileCommand.Continue(TileMoveMode.Run), 20);
        Assert.Equal(new TileCoord(3, 10, 0), s.Tile);
        Assert.Equal(0, s.InteractTarget);
    }

    [Fact]
    public void A_route_cap_outside_the_wires_own_throws_from_the_constructor()
    {
        TileCollisionMap map = Bake(FlatWorld());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TileMoveSimulator(map, Ticks, null, new TileMoveOptions { MaxRouteSteps = 0 }));
        // Above the wire's own cap the encoder refuses the route, and that refusal would otherwise land inside a
        // server tick on the first long click instead of here at construction.
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileMoveSimulator(
            map, Ticks, null, new TileMoveOptions { MaxRouteSteps = TileProtocol.MaxRouteSteps + 1 }));
        Assert.Equal(TileProtocol.MaxRouteSteps, new TileMoveSimulator(map, Ticks).MaxRouteSteps);
    }

    [Fact]
    public void A_path_radius_the_pathfinder_would_reject_throws_from_the_constructor()
    {
        TileCollisionMap map = Bake(FlatWorld());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TileMoveSimulator(map, Ticks, null, new TileMoveOptions { MaxPathRadius = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileMoveSimulator(
            map, Ticks, null, new TileMoveOptions { MaxPathRadius = TilePathfinder.MaxSearchRadius + 1 }));

        // The whole point of the guard is WHERE it throws. Unchecked, a zero radius builds a simulator that looks
        // fine and throws out of TilePathfinder on the first WalkTo, which on a server is inside a tick.
        var ok = new TileMoveSimulator(map, Ticks, null, new TileMoveOptions { MaxPathRadius = 1 });
        Assert.Equal(1, ok.MaxPathRadius);
        TileMoveState s = ok.Step(TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N),
            TileCommand.WalkTo(new TileCoord(0, 1, 0), TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Route.End);
    }

    [Fact]
    public void A_walk_to_another_plane_is_dropped_whole_and_never_coerced_onto_this_one()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), 3);

        TileMoveState dropped = sim.Step(s, TileCommand.WalkTo(new TileCoord(4, 2, 1), TileMoveMode.Walk), Dt);

        // Ignored means ignored: the tick is indistinguishable from one that carried no command at all, mode
        // included. The route is still the old one, not a fresh path to (4, 2) on the plane the player is on.
        Assert.Equal(sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt), dropped);
        Assert.Equal(new TileCoord(0, 6, 0), dropped.Route.End);
        Assert.Equal(TileMoveMode.Run, dropped.Mode);
        Assert.Equal(s.StepTotal, dropped.StepTotal);
    }

    [Fact]
    public void Holding_run_mid_walk_leaves_that_step_alone_and_runs_the_next_one()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Walk), Dt);

        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(TileMoveMode.Run, s.Mode);
        Assert.Equal(4, s.StepTotal);

        // The step under way keeps the walk cadence it started with: four ticks of glide, not two.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new Vector2(0f, 0.75f), s.Position);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new Vector2(0f, 1f), s.Position);
        Assert.Equal(2, s.StepTotal);                        // and the step starting here is a run

        // Which crosses the next tile in two ticks rather than four.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new Vector2(0f, 1.5f), s.Position);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new Vector2(0f, 2f), s.Position);
        Assert.Equal(new TileCoord(0, 3, 0), s.Tile);
    }

    [Fact]
    public void Dropping_run_mid_step_still_finishes_that_step_at_the_run_cadence()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), Dt);

        // Two ticks a step, one already spent, so the body lands on THIS tick even though the toggle is off, and
        // the step that starts as it lands is the one that takes the walking cadence.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new Vector2(0f, 1f), s.Position);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        Assert.Equal(4, s.StepTotal);

        // Four ticks to cross the tile, not two: three of them leave the body short of it.
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new Vector2(0f, 1.75f), s.Position);
        s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new Vector2(0f, 2f), s.Position);
        Assert.Equal(new TileCoord(0, 3, 0), s.Tile);
    }

    // Carried over from the task 2 review: StepFraction is what the presenter glides on, so a value outside 0 to 1
    // would put a remote past the tile it is walking into. The tick a step COMMITS is the one that could do it,
    // because that is the tick the count reaches the total, so the walk below covers several commits in both modes.
    [Theory]
    [InlineData(TileMoveMode.Walk)]
    [InlineData(TileMoveMode.Run)]
    public void The_step_fraction_stays_inside_zero_to_one_on_every_tick_including_a_commit(TileMoveMode mode)
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        TileCoord previous = s.Tile;
        int commits = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            TileCommand c = tick == 0
                ? TileCommand.WalkTo(new TileCoord(6, 6, 0), mode)
                : TileCommand.Continue(mode);
            s = sim.Step(s, c, Dt);
            if (!s.Tile.Equals(previous)) { commits++; previous = s.Tile; }
            Assert.InRange(s.StepFraction, 0f, 1f);
        }
        Assert.Equal(6, commits);
        Assert.Equal(new TileCoord(6, 6, 0), s.Tile);
    }
}
