using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileInteractTests
{
    const float Dt = 0.25f;
    static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    // A 2x1 interactive archetype, which the greybox catalog does not have: its two interactive archetypes are both
    // 1x1, and rotating a square rect gives the same rect back, so nothing else in the suite can tell a rotated
    // footprint from an unrotated one.
    const string WideBooth = """
        { "archetypes": [ { "id": "long_booth", "name": "Long booth", "meshRef": "kit/long_booth.glb",
                            "sizeX": 2, "sizeZ": 1, "collisionKind": "Solid", "interactive": true } ] }
        """;

    static (TileMoveSimulator sim, long boothId) World(params (int x, int z)[] walls)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        foreach ((int x, int z) in walls) doc.AddObject("tree", x, z, 0, 0);
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var targets = new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs);
        return (new TileMoveSimulator(map, Ticks, targets), booth.Id);
    }

    // The approach is the whole test. From due west the last step of the walk ALREADY points at the booth, so a
    // simulator that never turns on arrival passes by accident. Coming in from (9, 14) the walk arrives on the reach
    // tile at (10, 11) with a DIAGONAL last step, and no reach direction is ever diagonal, so this is the case that
    // holds the rule up.
    [Fact]
    public void An_interact_routes_to_a_reach_tile_and_faces_the_target_on_arrival()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(9, 14, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.Equal(booth, s.InteractTarget);
        Assert.False(s.Route.IsIdle);
        Assert.Equal(new TileCoord(10, 11, 0), s.Route.End);
        // Continue rather than None: the mode rides on every command, so a None here would quietly finish the run at
        // a walking cadence.
        for (int i = 0; i < 20 && !s.Route.IsIdle; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(10, 11, 0), s.Tile);
        Assert.Equal(booth, s.InteractTarget);
        Assert.Equal(TileDirection.S, s.Facing);
    }

    // The due-west approach, kept for what it pins that the case above cannot: TryNearest's scoring reaches the
    // simulator, so a click from five tiles west routes to the WEST reach tile rather than one of the other three.
    [Fact]
    public void The_nearest_reach_tile_is_the_one_the_player_comes_from()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(9, 10, 0), s.Route.End);
    }

    // The arrival turn is a SECOND document read inside the simulator, so pin that it stays a pure function of the
    // inputs: two instances over one command stream have to agree byte for byte through the arrival tick, or an
    // interaction walk snaps on reconcile instead of reconciling.
    [Fact]
    public void Two_instances_over_one_interact_stream_stay_byte_identical()
    {
        (TileMoveSimulator a, long booth) = World();
        (TileMoveSimulator b, long other) = World();
        Assert.Equal(booth, other);
        TileMoveState sa = TileMoveState.At(new TileCoord(9, 14, 0), TileDirection.N), sb = sa;
        for (int i = 0; i < 20; i++)
        {
            TileCommand c = i == 0
                ? TileCommand.Interact(booth, TileMoveMode.Run)
                : TileCommand.Continue(TileMoveMode.Run);
            sa = a.Step(sa, c, Dt);
            sb = b.Step(sb, c, Dt);
            Assert.Equal(sa, sb);
        }
        Assert.Equal(TileDirection.S, sa.Facing);
    }

    // The guard on the arrival turn, which is the half of the rule that looks like dead code. A re-path around a
    // blocker goes through FindPath, whose nearest-reachable fallback can leave a LIVE route that stops short of the
    // reach tile, and FacingToward answers W for a tile touching no footprint tile. So the route empties with the
    // target still pending, several tiles from the booth, and the player must keep the facing their last step gave
    // them rather than snapping west at nothing.
    [Fact]
    public void A_re_path_that_stops_short_of_the_reach_tile_does_not_turn()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var sim = new TileMoveSimulator(map, Ticks,
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs));

        TileMoveState s = TileMoveState.At(new TileCoord(0, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth.Id, TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(9, 10, 0), s.Route.End);
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(2, 10, 0), s.Tile);

        // One tree in the way so the next step fails and a re-path runs, and a sealed column so the re-path cannot
        // reach the booth at all. What is left reachable nearest the reach tile is (4, 10), so the re-path returns a
        // live two step route that ends four tiles short of anywhere the booth can be acted on.
        doc.AddObject("tree", 3, 10, 0, 0);
        for (int z = 0; z < TileRegion.Size; z++) doc.AddObject("tree", 5, z, 0, 0);
        TileCollisionBaker.Rebake(map, doc, TileMoveSimulatorTests.Catalogs,
            new TileRect(2, 0, 5, TileRegion.Size), 0);

        for (int i = 0; i < 20 && !s.Route.IsIdle; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Run), Dt);
        Assert.Equal(new TileCoord(4, 10, 0), s.Tile);
        Assert.Equal(booth.Id, s.InteractTarget);
        Assert.False(TileReach.Contains(map, TileFootprint.Of(TileMoveSimulatorTests.Catalogs.Archetype("bank_booth")!,
            10, 10, 0), 0, s.Tile));
        // The last step of the re-path, which came up onto (4, 10) from (4, 9) because the tree at (3, 10) denies the
        // diagonal into it. Unguarded, FacingToward would have written W here: no footprint tile touches (4, 10), so
        // it takes its fallback.
        Assert.Equal(TileDirection.N, s.Facing);
    }

    [Fact]
    public void An_interact_from_a_reach_tile_stands_still_and_turns_to_face()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(9, 10, 0), TileDirection.W);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Walk), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(TileDirection.E, s.Facing);
        Assert.Equal(booth, s.InteractTarget);
    }

    [Fact]
    public void A_walled_target_leaves_no_route_and_no_pending_target()
    {
        (TileMoveSimulator sim, long booth) = World((9, 10), (11, 10), (10, 9), (10, 11));
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        // Walk first, so the assertions below are a state the interact had to PRODUCE rather than the state it
        // started from. Without this, a BeginInteract that returned its input unchanged passes the case.
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(5, 14, 0), TileMoveMode.Run), Dt);
        Assert.False(s.Route.IsIdle);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(0, s.InteractTarget);
    }

    [Fact]
    public void A_non_interactive_or_unknown_target_is_not_a_target_at_all()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject tree = doc.AddObject("tree", 10, 10, 0, 0);
        var targets = new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs);
        Assert.False(targets.TryGetFootprint(tree.Id, out _, out _));
        Assert.False(targets.TryGetFootprint(999999, out _, out _));
    }

    // The rotation the class doc promises. A quarter turn swaps the footprint's width and height around the same SW
    // anchor, and reach is read off that rect, so an unrotated read would offer reach tiles beside a tile the object
    // does not stand on.
    [Fact]
    public void A_rotated_target_reports_the_rotated_footprint()
    {
        TileWorldCatalogs catalogs =
            TileWorldCatalogs.Merge(TileWorldCatalogs.Greybox(), TileWorldCatalogs.LoadJson(WideBooth, "wide"));
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject flat = doc.AddObject("long_booth", 10, 10, 0, 0);
        TileObject turned = doc.AddObject("long_booth", 20, 20, 1, 1);
        var targets = new TileDocumentTargets(doc, catalogs);

        Assert.True(targets.TryGetFootprint(flat.Id, out TileRect flatRect, out int flatPlane));
        Assert.Equal(new TileRect(10, 10, 2, 1), flatRect);
        Assert.Equal(0, flatPlane);
        Assert.True(targets.TryGetFootprint(turned.Id, out TileRect turnedRect, out int turnedPlane));
        Assert.Equal(new TileRect(20, 20, 1, 2), turnedRect);
        Assert.Equal(1, turnedPlane);
    }

    [Fact]
    public void A_walk_command_clears_a_pending_interaction()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.Equal(booth, s.InteractTarget);              // there IS a target for the walk to clear
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(2, 2, 0), TileMoveMode.Run), Dt);
        Assert.Equal(0, s.InteractTarget);
    }

    // The other half of the orchestrator's plane rule. A target on another plane is not a refusal the state records,
    // it is a command that never happened: BeginWalk already drops a cross-plane goal that way, and an Interact that
    // reset the cadence or cleared the target would stall a walk in progress on a click the player cannot even see.
    [Fact]
    public void A_cross_plane_interact_is_dropped_whole_the_way_a_cross_plane_walk_is()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject upstairs = doc.AddObject("bank_booth", 10, 10, 1, 0);
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), Ticks,
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs));

        // Mid step of a run, so a command that reset the cadence or the route would be visible.
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(5, 14, 0), TileMoveMode.Run), Dt);

        TileMoveState dropped = sim.Step(s, TileCommand.Interact(upstairs.Id, TileMoveMode.Walk), Dt);
        TileMoveState held = sim.Step(s, TileCommand.Continue(s.Mode), Dt);
        Assert.Equal(held, dropped);
        Assert.Equal(TileMoveMode.Run, dropped.Mode);
        Assert.False(dropped.Route.IsIdle);
    }

    // The refusal the cross-plane case is NOT. A same-plane target the map cannot resolve stops the player and drops
    // the target, which is the state a CannotReach answer is sent alongside.
    [Fact]
    public void An_unknown_target_on_this_plane_stops_the_walk_and_clears_the_target()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.False(s.Route.IsIdle);
        s = sim.Step(s, TileCommand.Interact(999999, TileMoveMode.Run), Dt);
        Assert.True(s.Route.IsIdle);
        Assert.Equal(0, s.InteractTarget);
    }

    // Accepts is what the server's command path asks BEFORE the step, so the four answers are pinned here rather
    // than inferred from what a step happened to leave behind. The last case is the one a careless implementation
    // gets wrong, and the one the whole CannotReach seam rests on: a target that does not resolve at all is
    // ACCEPTED, because "I cannot find what you clicked" is an answer the resolution owes the player, not a reason
    // to pretend the click never happened.
    [Fact]
    public void Accepts_answers_the_plane_rule_and_nothing_else()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject here = doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileObject upstairs = doc.AddObject("bank_booth", 20, 20, 1, 0);
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), Ticks,
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs));
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);

        Assert.False(sim.Accepts(s, TileCommand.WalkTo(new TileCoord(5, 14, 1), TileMoveMode.Run)));
        Assert.False(sim.Accepts(s, TileCommand.Interact(upstairs.Id, TileMoveMode.Run)));
        Assert.True(sim.Accepts(s, TileCommand.WalkTo(new TileCoord(5, 14, 0), TileMoveMode.Run)));
        Assert.True(sim.Accepts(s, TileCommand.Interact(here.Id, TileMoveMode.Run)));

        // Accepted is not "will succeed": an unresolved target still reaches the queue, and so does a Continue.
        Assert.True(sim.Accepts(s, TileCommand.Interact(999999, TileMoveMode.Run)));
        Assert.True(sim.Accepts(s, TileCommand.Continue(TileMoveMode.Run)));
    }

    // The plane is read off the STATE, not off the world, so the same command flips answer when the player moves
    // floors. That is what makes it safe for the server to ask before the step and for the client to ask on click.
    [Fact]
    public void Accepts_follows_the_players_own_plane()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject upstairs = doc.AddObject("bank_booth", 20, 20, 1, 0);
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), Ticks,
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs));

        TileCommand click = TileCommand.Interact(upstairs.Id, TileMoveMode.Run);
        Assert.False(sim.Accepts(TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N), click));
        Assert.True(sim.Accepts(TileMoveState.At(new TileCoord(5, 10, 1), TileDirection.N), click));
    }

    // A simulator with no target seam has no way to tell the planes apart, so it accepts every interaction and the
    // resolution answers them. Nothing about the walk half changes.
    [Fact]
    public void Accepts_admits_every_interaction_when_there_is_no_target_seam()
    {
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), Ticks);
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);

        Assert.True(sim.Accepts(s, TileCommand.Interact(1, TileMoveMode.Run)));
        Assert.False(sim.Accepts(s, TileCommand.WalkTo(new TileCoord(5, 14, 2), TileMoveMode.Run)));
    }

    [Fact]
    public void The_queue_holds_one_action_per_player_and_a_second_click_replaces_it()
    {
        var q = new TileActionQueue();
        q.Issue(slot: 3, target: 11, issuedTick: 100);
        q.Issue(slot: 3, target: 22, issuedTick: 101);
        q.Issue(slot: 4, target: 33, issuedTick: 101);
        Assert.Equal(2, q.PendingCount);
        Assert.True(q.TryPeek(3, out TilePendingAction a));
        Assert.Equal(22, a.Target);
        Assert.Equal(101, a.IssuedTick);
        q.Clear(3);
        Assert.False(q.TryPeek(3, out _));
        q.Forget(4);
        Assert.Equal(0, q.PendingCount);
    }
}
