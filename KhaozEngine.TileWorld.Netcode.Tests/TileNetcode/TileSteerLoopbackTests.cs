using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Steering over the two real heads: the held level on the client, the admission case on the server, and the
/// cadence the pair agree on. The simulator suites already pin what one stepper does with a steer, so what is left
/// here is only what the wire and the two clocks add to it.
/// </summary>
public class TileSteerLoopbackTests
{
    static readonly TileCoord Spawn = new(10, 10, 0);

    // The harness pumps FRAMES and a server tick is five of them, so this is the plan's tick pump written in the
    // harness's own clock. The client's phased clock sends exactly one command into each of those ticks, so a pump
    // of n after the level is set is n steered ticks on the server.
    static void Pump(TileLoopbackHarness h, int ticks) => h.Frames(ticks * 5);

    // Long enough for the join, the first snapshot and the seed, and it leaves the two clocks on the alignment
    // every arithmetic comment below counts from: the client's next command tick is the one the server's next tick
    // dequeues.
    static TileLoopbackHarness Joined(TileWorldDocument serverDoc, TileWorldDocument clientDoc,
        System.Func<int, bool>? canRun = null)
    {
        var h = new TileLoopbackHarness(serverDoc, clientDoc, Spawn, clientPhase: 0.13f, canRun: canRun);
        h.Frames(20);
        Assert.True(h.Client.IsJoined);
        return h;
    }

    [Fact]
    public void A_steering_client_walks_in_step_with_the_server_and_is_never_corrected()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld());

        h.Client.SetSteering(TileDirection.N, TileMoveMode.Walk);
        Pump(h, 20);

        // Walk is four ticks a step and the standing door counts its first tick immediately, so twenty steered
        // ticks commit on ticks 1, 4, 8, 12, 16 and 20. Six tiles, and the client predicted every one of them a
        // tick before the server applied it.
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(10, 16, 0), server.Tile);
        Assert.Equal(server.Tile, h.Client.Prediction.PredictedState.Tile);
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);
    }

    [Fact]
    public void Releasing_the_level_stops_the_body_on_the_tile_it_was_walking_into()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld());

        h.Client.SetSteering(TileDirection.N, TileMoveMode.Walk);
        Pump(h, 2);
        h.Client.SetSteering(null, TileMoveMode.Walk);
        Pump(h, 10);

        // The step the first steered tick committed is the LAST one: releasing the level makes the next command a
        // Continue, so the glide lands on the tile it already owned and nothing starts behind it.
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(10, 11, 0), server.Tile);
        Assert.False(server.IsStepping);
        TileMoveState predicted = h.Client.Prediction.PredictedState;
        Assert.Equal(new TileCoord(10, 11, 0), predicted.Tile);
        Assert.False(predicted.IsStepping);
        Assert.Null(h.Client.Steering);
    }

    [Fact]
    public void A_queued_click_wins_its_tick_and_steering_resumes_on_the_next()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld());

        h.Client.SetSteering(TileDirection.E, TileMoveMode.Walk);
        Pump(h, 1);
        // The click owns its own tick, so the route west is genuinely built on both heads. Steering takes the very
        // next tick back off it, which is the ordering this test exists for: a level that lost to the click would
        // walk west, and one that beat it would never have built the route at all.
        h.Client.Queue(TileCommand.WalkTo(new TileCoord(2, 10, 0), TileMoveMode.Walk));
        Pump(h, 20);

        // Twenty steered ticks with one spent on the click, and the click writes no step progress, so the commits
        // fall on the same 1, 4, 8, 12, 16 and 20 a clean hold gives. Six tiles EAST, away from the goal.
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(16, 10, 0), server.Tile);
        Assert.True(server.Route.IsIdle);
        TileMoveState predicted = h.Client.Prediction.PredictedState;
        Assert.Equal(server.Tile, predicted.Tile);
        Assert.True(predicted.Route.IsIdle);
    }

    [Fact]
    public void The_steering_mode_is_not_adopted_as_the_run_toggle()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld());
        Assert.Equal(TileMoveMode.Walk, h.Client.RunMode);

        h.Client.SetSteering(TileDirection.N, TileMoveMode.Run);
        Pump(h, 4);

        // The level's mode rides the steered steps and nothing else. A head that inverted the pace with a modifier
        // would otherwise find the toggle rewritten under it the way a click rewrites it.
        Assert.Equal(TileMoveMode.Walk, h.Client.RunMode);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState running));
        Assert.Equal(TileMoveMode.Run, running.Mode);

        h.Client.SetSteering(null, TileMoveMode.Run);
        Pump(h, 6);

        // The saved toggle is what the next Continue carries, so the body is back at the walking pace the head
        // never changed.
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState released));
        Assert.Equal(TileMoveMode.Walk, released.Mode);
        Assert.Equal(TileMoveMode.Walk, h.Client.Prediction.PredictedState.Mode);
    }

    [Fact]
    public void A_gated_run_steers_at_walk_and_is_counted()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld(),
            canRun: _ => false);

        h.Client.SetSteering(TileDirection.N, TileMoveMode.Run);
        Pump(h, 8);

        // The gate runs over every admitted command whatever its kind, so a steered run is stepped at the walking
        // cadence: commits on ticks 1, 4 and 8 rather than the 1, 2, 4, 6 and 8 a run would have given. Three tiles
        // where five were asked for.
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(TileMoveMode.Walk, server.Mode);
        Assert.Equal(new TileCoord(10, 13, 0), server.Tile);
        Assert.True(h.Server.GatedRunCount > 0);
    }

    [Fact]
    public void Steering_clears_the_pending_action_a_click_queued()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = serverDoc.AddObject("bank_booth", 16, 10, 0, 0);
        TileWorldDocument clientDoc = TileMoveSimulatorTests.FlatWorld();
        clientDoc.AddObject("bank_booth", 16, 10, 0, 0);
        using TileLoopbackHarness h = Joined(serverDoc, clientDoc);
        var acted = new List<long>();
        var refused = new List<long>();
        var notices = new List<string>();
        h.Server.OnInteract += (_, _, target) => acted.Add(target);
        h.Server.OnCannotReach += (_, target) => refused.Add(target);
        h.Client.NoticeReceived += notices.Add;

        h.Client.Queue(TileCommand.Interact(booth.Id, TileMoveMode.Walk));
        Pump(h, 1);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState approaching));
        Assert.Equal(booth.Id, approaching.InteractTarget);
        Assert.False(approaching.Route.IsIdle);

        // Steering away is the player visibly abandoning the click, so the queue entry has to go with the route the
        // steer emptied. An entry that outlived it reads the emptied route as an ARRIVAL at a target the state no
        // longer names, which is the CannotReach case.
        h.Client.SetSteering(TileDirection.N, TileMoveMode.Walk);
        Pump(h, 10);
        h.Client.SetSteering(null, TileMoveMode.Walk);
        Pump(h, 10);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState standing));
        Assert.True(standing.Route.IsIdle);
        Assert.False(standing.IsStepping);
        Assert.Equal(0L, standing.InteractTarget);
        Assert.Empty(acted);
        Assert.Empty(refused);
        Assert.DoesNotContain(TileServerReason.CannotReach, notices);
    }

    [Fact]
    public void Steering_out_of_a_fight_is_a_disengage_rather_than_a_broken_lock()
    {
        using TileLoopbackHarness h = Joined(TileMoveSimulatorTests.FlatWorld(), TileMoveSimulatorTests.FlatWorld());
        long actor = h.Server.SpawnActor(new TileCoord(10, 20, 0), new TileActorSpawn(30, 10, TileDirection.S));
        var refused = new List<(int slot, long target)>();
        var notices = new List<string>();
        h.Server.OnCannotReach += (slot, target) => refused.Add((slot, target));
        h.Client.NoticeReceived += notices.Add;

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Walk));
        Pump(h, 2);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState chasing));
        Assert.Equal(actor, chasing.CombatTarget);
        Assert.False(chasing.Route.IsIdle);

        // The tick's own steer is what breaks this lock, and the fight-lock watch has to read that the way it reads
        // a click: the player DISENGAGED, so there is nothing owed to them. A watch that missed the steer would see
        // a lock cleared by the movement pass whose target still resolves, which is the failure-to-reach case, and
        // would answer a deliberate walk away with a CannotReach the client is holding a pending attack against.
        h.Client.SetSteering(TileDirection.S, TileMoveMode.Walk);
        Pump(h, 4);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState left));
        Assert.Equal(0L, left.CombatTarget);
        Assert.True(left.Route.IsIdle);
        // Two ticks of the chase committed one step north and left the body mid glide, so the first steered tick
        // finishes that step and the landing tick commits the step back south. The body is on the tile it joined
        // on, walking away from the fight rather than standing in it.
        Assert.Equal(new TileCoord(10, 10, 0), left.Tile);
        Assert.Empty(refused);
        Assert.DoesNotContain(TileServerReason.CannotReach, notices);
    }
}
