using System;
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

    [Fact]
    public void A_run_commits_a_tile_every_two_ticks_and_a_walk_every_four()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Run), 1);
        Assert.Equal(new TileCoord(0, 0, 0), s.Tile);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        Assert.Equal(0, s.StepTicks);

        TileMoveState w = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        w = Run(sim, w, TileCommand.WalkTo(new TileCoord(0, 4, 0), TileMoveMode.Walk), 3);
        Assert.Equal(new TileCoord(0, 0, 0), w.Tile);
        w = sim.Step(w, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 1, 0), w.Tile);
    }

    [Fact]
    public void A_diagonal_step_costs_the_same_as_a_cardinal_one_and_sets_the_facing()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(3, 3, 0), TileMoveMode.Run), 2);
        Assert.Equal(new TileCoord(1, 1, 0), s.Tile);
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
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);

        // Wall the whole row off, then keep ticking: the re-path fails and the route is dropped. The row spans
        // the region because the world either side of it is open, so a few tiles would only make the walk longer.
        for (int x = 0; x < TileRegion.Size; x++) doc.AddObject("tree", x, 3, 0, 0);
        TileCollisionBaker.Rebake(map, doc, Catalogs, new TileRect(0, 2, TileRegion.Size, 3), 0);
        for (int i = 0; i < 6; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
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

    [Fact]
    public void A_second_walk_command_replaces_the_route_from_where_the_player_stands()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = Run(sim, s, TileCommand.WalkTo(new TileCoord(0, 8, 0), TileMoveMode.Run), 4);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(4, 2, 0), TileMoveMode.Walk), Dt);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        Assert.Equal(4, s.StepTotal);
        // One, not zero: the command reset step progress and then this same tick advanced the new step by one.
        Assert.Equal(1, s.StepTicks);
        Assert.Equal(new TileCoord(4, 2, 0), s.Route.End);
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
        // walked toward and no further. The pending target is still remembered: the arrival turn is guarded by
        // TileReach.Contains, which declines for a player who stopped short of the reach set.
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
        Assert.Equal(booth.Id, s.InteractTarget);
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

        // The step under way keeps the walk cadence it started with: four ticks, not two.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 0, 0), s.Tile);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        Assert.Equal(2, s.StepTotal);

        // The next one is a run.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
    }

    [Fact]
    public void Dropping_run_mid_step_still_finishes_that_step_at_the_run_cadence()
    {
        TileMoveSimulator sim = Sim(Bake(FlatWorld()));
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(0, 6, 0), TileMoveMode.Run), Dt);

        // Two ticks a step, one already spent, so this step commits on THIS tick even though the toggle is off.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        Assert.Equal(4, s.StepTotal);

        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 1, 0), s.Tile);
        s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(0, 2, 0), s.Tile);
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
