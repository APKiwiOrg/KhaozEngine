using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileInteractTests
{
    const float Dt = 0.25f;
    static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    static (TileMoveSimulator sim, long boothId) World(params (int x, int z)[] walls)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        foreach ((int x, int z) in walls) doc.AddObject("tree", x, z, 0, 0);
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var targets = new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs);
        return (new TileMoveSimulator(map, Ticks, targets), booth.Id);
    }

    [Fact]
    public void An_interact_routes_to_a_reach_tile_and_faces_the_target_on_arrival()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
        Assert.Equal(booth, s.InteractTarget);
        Assert.Equal(new TileCoord(9, 10, 0), s.Route.End);
        for (int i = 0; i < 20 && !s.Route.IsIdle; i++) s = sim.Step(s, TileCommand.None, Dt);
        Assert.Equal(new TileCoord(9, 10, 0), s.Tile);
        Assert.Equal(booth, s.InteractTarget);
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

    [Fact]
    public void A_walk_command_clears_a_pending_interaction()
    {
        (TileMoveSimulator sim, long booth) = World();
        TileMoveState s = TileMoveState.At(new TileCoord(5, 10, 0), TileDirection.N);
        s = sim.Step(s, TileCommand.Interact(booth, TileMoveMode.Run), Dt);
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
