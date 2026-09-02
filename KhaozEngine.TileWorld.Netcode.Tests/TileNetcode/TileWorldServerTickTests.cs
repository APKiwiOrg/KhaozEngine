using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldServerTickTests
{
    const float Dt = 0.25f;

    internal static TileWorldServerConfig Config(TileCoord spawn) => new()
    {
        TickSeconds = Dt,
        StepTicks = new TileStepTicks(walk: 4, run: 2),
        Spawn = spawn,
        MaxPlayers = 8,
    };

    internal static TileWorldServer Server(TileWorldDocument doc, INetTransport transport, TileCoord spawn) =>
        new(transport, Config(spawn), TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());

    [Fact]
    public void A_spawned_player_stands_on_the_spawn_tile_in_its_own_cell()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnPlayer(slot: 0, accountId: "a", displayName: "Ari");
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(10, 10, 0), st.Tile);
        Assert.True(s.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.Equal(new CellCoord(0, 0), cell.Coord);
        Assert.Equal(1, s.PlayerCount);
    }

    [Fact]
    public void A_queued_walk_steps_one_tile_every_two_ticks_at_run()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        // Two run ticks is one whole step of BODY, and the tile is a step ahead of it: the step into (10, 12) was
        // committed on the tick the body landed on (10, 11).
        Assert.Equal(new Vector2(10f, 11f), st.Position);
        Assert.Equal(new TileCoord(10, 12, 0), st.Tile);
        Assert.Equal(2, s.TickCount);
        Assert.Equal(2, st.StepTotal);
    }

    [Fact]
    public void The_tick_raises_OnBeforeTick_once_per_tick_before_movement()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(1, 1, 0));
        s.SpawnPlayer(0, "a", "Ari");
        var tilesSeen = new List<TileCoord>();
        s.OnBeforeTick += _ => { s.TryGetPlayerState(0, out TileMoveState st); tilesSeen.Add(st.Tile); };
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(1, 3, 0), TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(3, tilesSeen.Count);
        // The first hook runs before the walk is ever drained, so it sees the spawn tile. The second sees the tile
        // committed on tick one, and the third the tile committed as the body landed on it.
        Assert.Equal(new TileCoord(1, 1, 0), tilesSeen[0]);
        Assert.Equal(new TileCoord(1, 2, 0), tilesSeen[1]);
        Assert.Equal(new TileCoord(1, 3, 0), tilesSeen[2]);
    }

    // The whole tick body runs once per WHOLE TickSeconds, through the server's own accumulator, so a caller on a
    // frame clock can never separate a drained command from the step it feeds. Sixty frames of a 60 Hz caller is one
    // second, which is four whole ticks at a 250 ms tick, and it has to land byte-identically on what four whole-tick
    // calls produce. Driven per call instead, the walk is drained on frame one and overwritten by the starvation
    // neutral on frame two, before any simulator ever sees it, and the player never leaves the spawn tile.
    [Fact]
    public void Sixty_short_frames_step_the_whole_ticks_they_add_up_to()
    {
        var hubA = new InMemoryTransportHub();
        var hubB = new InMemoryTransportHub();
        using TileWorldServer frames =
            Server(TileMoveSimulatorTests.FlatWorld(), hubA.Server, new TileCoord(10, 10, 0));
        using TileWorldServer whole =
            Server(TileMoveSimulatorTests.FlatWorld(), hubB.Server, new TileCoord(10, 10, 0));
        foreach (TileWorldServer s in new[] { frames, whole })
        {
            s.SpawnPlayer(0, "a", "Ari");
            s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        }

        for (int i = 0; i < 60; i++) frames.Tick(1f / 60f);
        for (int i = 0; i < 4; i++) whole.Tick(Dt);

        Assert.Equal(4, frames.TickCount);
        Assert.Equal(whole.TickCount, frames.TickCount);
        Assert.True(frames.TryGetPlayerState(0, out TileMoveState a));
        Assert.True(whole.TryGetPlayerState(0, out TileMoveState b));
        Assert.Equal(b, a);
        Assert.Equal(new TileCoord(10, 13, 0), a.Tile);
        Assert.Equal(new Vector2(10f, 12f), a.Position);
    }

    // The same rule at the sub-tick end, on a frame length that is exact in binary so nothing here turns on float
    // accumulation: a quarter of a tick four times is one tick, and three of them are not a tick at all.
    [Fact]
    public void Four_quarter_frames_move_the_player_exactly_as_one_whole_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));

        for (int i = 0; i < 3; i++) s.Tick(Dt / 4f);
        Assert.Equal(0, s.TickCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState pending));
        Assert.True(pending.Route.IsIdle);   // nothing drained, so nothing to overwrite

        s.Tick(Dt / 4f);
        Assert.Equal(1, s.TickCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState stepped));
        Assert.Equal(new TileCoord(10, 20, 0), stepped.Route.End);
        Assert.Equal(1, stepped.StepTicks);
    }

    // A host that fell far behind (a stall, a debugger break, a long GC) sheds the backlog rather than trying to run
    // every tick it missed, which is the rule FixedTickHost uses and the reason it has one: catching up 400 ticks
    // takes longer than real time and leaves the next frame further behind still.
    [Fact]
    public void A_long_stall_sheds_its_backlog_past_the_catch_up_cap()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");

        s.Tick(100f);   // 400 ticks' worth of elapsed time

        Assert.Equal(8, s.TickCount);   // MaxCatchUpTicks, not 400

        // And the 392 ticks it did NOT run are GONE rather than owed, which is the half the cap does not give you.
        // The accumulator kept at most one tick's worth, so the very next frame steps promptly and then stops.
        // Without the shed it carries 98 seconds forward and runs another full eight ticks on this frame, and on
        // the four hundred frames after it, which is the spiral.
        s.Tick(Dt);
        Assert.InRange(s.TickCount, 9L, 10L);   // this frame's tick, plus at most the one tick the shed kept
    }

    // The tick order's first step, pinned on its own: ONE command per player per tick, oldest first. Both commands
    // are buffered before the first tick, so a drain that emptied the queue would apply the second one immediately
    // and the route would already point east on tick one.
    [Fact]
    public void The_tick_drains_exactly_one_command_per_player()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Enqueue(0, 1, TileCommand.WalkTo(new TileCoord(14, 10, 0), TileMoveMode.Run));

        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState first));
        Assert.Equal(new TileCoord(10, 14, 0), first.Route.End);

        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState second));
        Assert.Equal(new TileCoord(14, 10, 0), second.Route.End);
    }

    // The starvation rule. The neutral command is Continue(state.Mode), never TileCommand.None: None is
    // Continue(Walk), which is a run TOGGLED OFF, so a player whose packets stop arriving would silently drop to a
    // walking cadence mid route. With the wrong neutral this lands on (10, 12) at a walking StepTotal of 4.
    [Fact]
    public void A_running_player_whose_queue_starves_keeps_running()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        for (int i = 0; i < 6; i++) s.Tick(Dt);   // one command, then five starved ticks

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(10, 14, 0), st.Tile);
        Assert.Equal(new Vector2(10f, 13f), st.Position);
        Assert.Equal(TileMoveMode.Run, st.Mode);
        Assert.Equal(2, st.StepTotal);
    }

    // A goal past MaxGoalRadius is dropped rather than pathed, but the run toggle it carried still applies: the
    // rewrite is Continue(cmd.Mode), not TileCommand.None.
    [Fact]
    public void An_out_of_range_walk_is_dropped_and_still_carries_the_run_toggle()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 1000, 0), TileMoveMode.Run));
        s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(10, 10, 0), st.Tile);
        Assert.True(st.Route.IsIdle);
        Assert.Equal(TileMoveMode.Run, st.Mode);
    }

    // A cross-plane interact is dropped WHOLE by the simulator, so it must never reach the action queue either: a
    // queued entry would come ready the moment the player wandered onto a reach tile of a target on another floor.
    [Fact]
    public void A_cross_plane_interact_never_reaches_the_action_queue()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 1, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(10, 14, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        s.Tick(Dt);

        Assert.Equal(0, s.Actions.PendingCount);
        // The command still reaches the simulator verbatim, which drops it whole, so not even the mode is applied.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(TileMoveMode.Walk, st.Mode);
        Assert.True(st.Route.IsIdle);
    }

    // The other half of the abandonment rule: an applied WalkTo clears the pending action, because the simulator
    // clears the state's own InteractTarget on a walk and an entry that outlived it would fire on the way past.
    [Fact]
    public void An_accepted_interact_queues_and_an_applied_walk_clears_it()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(14, 14, 0));
        s.SpawnPlayer(0, "a", "Ari");

        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        s.Tick(Dt);
        Assert.Equal(1, s.Actions.PendingCount);

        s.Enqueue(0, 1, TileCommand.WalkTo(new TileCoord(20, 20, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.Equal(0, s.Actions.PendingCount);
    }

    // The arrival half of the action queue: the walk to a reach tile ends, the action fires exactly once, and the
    // state's own pending target is cleared with it so no later walk that happens to end in reach fires it again.
    [Fact]
    public void An_interact_raises_OnInteract_once_on_arrival()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(5, 10, 0));
        long netId = s.SpawnPlayer(0, "a", "Ari");
        var raised = new List<(int slot, long player, long target)>();
        s.OnInteract += (slot, player, target) => raised.Add((slot, player, target));

        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        for (int i = 0; i < 12; i++) s.Tick(Dt);   // four steps at run cadence, then four spare ticks

        Assert.Equal(new[] { (0, netId, booth.Id) }, raised);
        Assert.Equal(0, s.Actions.PendingCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(9, 10, 0), st.Tile);
        Assert.Equal(0, st.InteractTarget);
    }

    // MaxCommandsPerSecond admits ten commands per 250 ms tick against a drain of one, so a burst can hand the
    // queue far more than it will ever consume one per tick. Past the catch-up threshold the stale ones are shed
    // and the newest is applied, so the server is never left walking input from a minute ago. Without it the first
    // tick applies seq 0 and the player walks north for the next thirty-nine ticks.
    [Fact]
    public void A_command_burst_is_shed_rather_than_replayed_one_per_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");

        for (int seq = 0; seq < 39; seq++)
            s.Enqueue(0, seq, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Walk));
        s.Enqueue(0, 39, TileCommand.WalkTo(new TileCoord(20, 10, 0), TileMoveMode.Run));

        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState first));
        Assert.Equal(new TileCoord(20, 10, 0), first.Route.End);
        Assert.Equal(TileMoveMode.Run, first.Mode);

        // And nothing stale is left behind to re-route the player on the ticks after.
        for (int i = 0; i < 4; i++) s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState later));
        Assert.Equal(new TileCoord(20, 10, 0), later.Route.End);
        Assert.Equal(new TileCoord(13, 10, 0), later.Tile);
    }

    // SetPlayerState is a door, so what it cannot accept is refused THERE. A route over the cap would otherwise be
    // written happily and then throw out of the snapshot encoder on the next serve, killing that tick for every
    // other player on the server, which is the thing this codebase keeps refusing on purpose.
    [Fact]
    public void SetPlayerState_refuses_a_state_the_tick_could_not_survive()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.SpawnPlayer(1, "b", "Bo");

        var steps = new TileDirection[300];   // the cap is TileProtocol.MaxRouteSteps, 256
        Array.Fill(steps, TileDirection.N);
        TileMoveState tooLong = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.S);
        tooLong.Route = TileRoute.FromSteps(tooLong.Tile, steps);
        ArgumentException route = Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, tooLong));
        Assert.Contains("300", route.Message);

        // The other two doors, refused for the same reason: a player nobody can see and who can never step.
        Assert.Throws<ArgumentException>(() =>
            s.SetPlayerState(0, TileMoveState.At(new TileCoord(10, 10, 9), TileDirection.S)));
        Assert.Throws<ArgumentException>(() =>
            s.SetPlayerState(0, TileMoveState.At(new TileCoord(9000, 10, 0), TileDirection.S)));

        // Nothing was written and the tick runs on, for the refused player and for everybody else.
        s.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(2, s.TickCount);
        Assert.True(s.TryGetPlayerState(1, out TileMoveState other));
        Assert.Equal(new TileCoord(10, 12, 0), other.Tile);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState refused));
        Assert.Equal(new TileCoord(10, 10, 0), refused.Tile);
        Assert.True(refused.Route.IsIdle);
    }

    // The last refusal, the one delegated to TileRoute.RemainingSteps, is the one that has to happen before the
    // first write rather than between the two. It runs on the ROUTE rather than the tile, and taken after the state
    // was written it left the entity holding a route the simulator cannot walk: TileMoveSimulator.Advance asks
    // TileRoute.Direction for the step between two tiles that are not adjacent on the very next tick, and that throw
    // comes out of host.Tick and takes the tick down for every player on the server. The reachable form is a
    // persistence restore or an admin move that sets the tile without rebuilding the route from it.
    [Fact]
    public void SetPlayerState_refuses_a_route_that_does_not_walk_from_the_state_tile()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.SpawnPlayer(1, "b", "Bo");

        // Passes all three of the checks above (plane 0, a loaded region, one step against a cap of 256) and fails
        // only on the gap: ten tiles of it, spelled as a single step.
        TileMoveState detached = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.S);
        detached.Route = new TileRoute(new[] { new TileCoord(20, 20, 0) }, 0);
        ArgumentException gap = Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, detached));
        Assert.Contains("not adjacent", gap.Message);

        // The previous state is intact, which is what the refusal promises: still on the spawn tile, still idle.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState refusedRoute));
        Assert.Equal(new TileCoord(10, 10, 0), refusedRoute.Tile);
        Assert.True(refusedRoute.Route.IsIdle);

        // And the tick runs, for the refused player and for everybody else.
        s.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(2, s.TickCount);
        Assert.True(s.TryGetPlayerState(1, out TileMoveState walker));
        Assert.Equal(new TileCoord(10, 12, 0), walker.Tile);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState standing));
        Assert.Equal(new TileCoord(10, 10, 0), standing.Tile);
        Assert.True(standing.Route.IsIdle);
    }

    // The state door is the ONE write path that does not run through the simulator, so it is the only way a glide
    // origin the stepper could never have produced can exist at all. The reachable form is the natural admin-move
    // idiom: copy the live state, change Tile, keep mode, epoch and pending target. The server then reads a phantom
    // step in flight and cannot start the next route step for a whole StepTotal, while the DECODER clamps the same
    // state back to standing on its way to the owner, so the two heads hold different states for the length of the
    // phantom step and every snapshot in it reports a correction that moves nothing.
    [Fact]
    public void SetPlayerState_refuses_a_step_origin_the_simulator_could_not_have_produced()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.SpawnPlayer(1, "b", "Bo");

        // The copied-state idiom with no step progress at all: the origin is left ten tiles behind the new tile.
        // Zero ticks do not make it standing, because IsStepping is the tile PAIR and nothing else.
        TileMoveState moved = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.S);
        moved.Tile = new TileCoord(20, 10, 0);
        Assert.Equal(0, (int)moved.StepTicks);
        ArgumentException far = Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, moved));
        Assert.Contains("StepFrom", far.Message);

        // The same gap taken mid-glide, which is what a copy made while the player was walking looks like.
        TileMoveState progressed = moved;
        progressed.StepTicks = 2;
        progressed.StepTotal = 4;
        Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, progressed));

        // An origin on ANOTHER plane, which no step produces because a step never changes plane. It is unspellable
        // on the wire (StepFrom rides without a plane byte of its own), so this door is the only place a hand-built
        // one can be caught.
        TileMoveState crossPlane = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.S);
        crossPlane.StepFrom = new TileCoord(10, 10, 1);
        Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, crossPlane));

        // What the door must NOT refuse: a real step in flight. Zero progress is legal on one, because the tick a
        // body lands is the tick the next step commits, so a landing tick carries an origin and no ticks yet.
        TileMoveState stepping = TileMoveState.At(new TileCoord(10, 11, 0), TileDirection.N);
        stepping.StepFrom = new TileCoord(10, 10, 0);
        stepping.StepTotal = 4;
        s.SetPlayerState(0, stepping);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState accepted));
        Assert.Equal(new TileCoord(10, 10, 0), accepted.StepFrom);
        Assert.True(accepted.IsStepping);

        // And the tick runs on, for everybody else as well.
        s.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        Assert.Equal(2, s.TickCount);
        Assert.True(s.TryGetPlayerState(1, out TileMoveState walker));
        Assert.Equal(new TileCoord(10, 12, 0), walker.Tile);
    }

    // A teleport CUTS, and the epoch alone does not make it cut. An origin one step from the destination is a
    // perfectly legal step, so a ONE TILE teleport built by copying a state that was mid-glide rides the wire
    // intact and gets glided in like an ordinary step, which is the one thing the flag exists to stop. Every other
    // caller in the tree happens to pass TileMoveState.At, which seeds the origin onto the tile, so nothing
    // exercised the other idiom until this.
    [Fact]
    public void A_one_tile_teleport_cuts_whatever_origin_it_was_handed()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState walking));
        Assert.True(walking.IsStepping);

        // Copy the mid-glide state and move the tile one on, the admin idiom that keeps mode, epoch and target.
        TileMoveState placed = walking;
        placed.Tile = walking.Tile.Offset(1, 0);
        placed.Route = TileRoute.None;
        Assert.True(placed.IsStepping);   // the origin is Chebyshev-1 from the new tile, so it IS a legal step
        s.SetPlayerState(0, placed, teleport: true);

        // In memory is the right place to look: the door writes the normalized state, so the wire never sees
        // the stale origin at all, and the codec round trip is covered by the I1 protocol tests.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(placed.Tile, after.Tile);
        Assert.Equal(after.Tile, after.StepFrom);
        Assert.False(after.IsStepping);
        Assert.Equal(0, (int)after.StepTicks);
        Assert.Equal(new Vector2(after.Tile.X, after.Tile.Z), after.Position);
        Assert.Equal(walking.Epoch + 1u, after.Epoch);
    }

    // The FAR half of the same idiom, and the reason the door normalizes a teleport BEFORE validating: a
    // teleport across the map copied from a mid-glide state carries an origin that is not a legal step, and a
    // door that validated the raw state first threw on the exact placement the flag exists for.
    [Fact]
    public void A_far_teleport_is_never_refused_for_the_stale_origin_it_was_handed()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState walking));
        Assert.True(walking.IsStepping);

        TileMoveState placed = walking;
        placed.Tile = walking.Tile.Offset(3, 3);
        placed.Route = TileRoute.None;
        Assert.False(TileMoveState.IsStepOrigin(placed.StepFrom, placed.Tile));
        s.SetPlayerState(0, placed, teleport: true);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(placed.Tile, after.Tile);
        Assert.Equal(after.Tile, after.StepFrom);
        Assert.False(after.IsStepping);
    }

    // A legal origin with impossible progress is the same class of simulator-unproducible pair the origin
    // refusal closes, so the door refuses it on the same terms. The teleport form of the same state is fine,
    // because the teleport normalizes progress away before the validation runs.
    [Fact]
    public void The_door_refuses_progress_at_or_past_the_steps_total()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState walking));
        Assert.True(walking.IsStepping);

        TileMoveState tampered = walking;
        tampered.StepTicks = 250;
        Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, tampered));
        s.SetPlayerState(0, tampered, teleport: true);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(0, (int)after.StepTicks);
    }

    // Two actions coming ready on the SAME tick resolve oldest CLICK first, which is what TilePendingAction's
    // IssuedTick is for. The setup makes every other order wrong: slot 1 joins first, so the player index's own
    // enumeration order is [1, 5] and so is ascending slot order, while slot 5 clicked two ticks earlier. Slot 5
    // walks three steps from the click on tick 0 and slot 1 walks two from the click on tick 2, so both arrive on
    // tick 5 and the tie is broken by nothing but the issue tick.
    [Fact]
    public void Two_actions_ready_on_one_tick_resolve_oldest_click_first()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject west = doc.AddObject("bank_booth", 10, 10, 0, 0);
        TileObject east = doc.AddObject("bank_booth", 30, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(10, 20, 0));
        s.SpawnPlayer(1, "a", "Ari");
        s.SpawnPlayer(5, "b", "Bo");
        s.SetPlayerState(5, TileMoveState.At(new TileCoord(14, 10, 0), TileDirection.S));   // 3 steps to (11, 10)
        s.SetPlayerState(1, TileMoveState.At(new TileCoord(33, 10, 0), TileDirection.S));   // 2 steps to (31, 10)
        var raised = new List<(int slot, long tick)>();
        s.OnInteract += (slot, _, _) => raised.Add((slot, s.TickCount));

        s.Enqueue(5, 0, TileCommand.Interact(west.Id, TileMoveMode.Run));
        s.Tick(Dt);   // tick 0, slot 5 clicks
        s.Tick(Dt);
        s.Enqueue(1, 0, TileCommand.Interact(east.Id, TileMoveMode.Run));
        for (int i = 0; i < 6; i++) s.Tick(Dt);   // tick 2, slot 1 clicks, then both walks finish

        Assert.Equal(2, raised.Count);
        Assert.Equal(raised[0].tick, raised[1].tick);          // the same tick, so the order IS the decision
        Assert.Equal(new[] { 5, 1 }, raised.ConvertAll(r => r.slot));
    }

    // The arrival test is the state's ROUTE, and a cell handoff rebuilds the state from a capture whose move-state
    // codec omits the route. Read off the raw component, a player crossing a region boundary mid walk reads as
    // arrived and the action fires a region early, on a tile nowhere near the target.
    [Fact]
    public void An_interact_across_a_region_boundary_does_not_fire_early()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileObject booth = doc.AddObject("bank_booth", 68, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(60, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        var raised = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);

        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        for (int i = 0; i < 10; i++) s.Tick(Dt);   // past x = 64, so the handoff has already happened

        Assert.True(s.TryGetPlayerState(0, out TileMoveState crossing));
        Assert.True(crossing.Tile.X > 64);
        Assert.False(crossing.Route.IsIdle);
        Assert.Empty(raised);

        for (int i = 0; i < 10; i++) s.Tick(Dt);
        Assert.Equal(new[] { booth.Id }, raised);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState arrived));
        Assert.Equal(new TileCoord(67, 10, 0), arrived.Tile);
    }

    // Planes do not shard, so the plane filter has to live in the serve. Both players are in the one cell and nine
    // tiles apart, well inside the interest radius, and neither may see the other.
    //
    // Through the internal serve on purpose, because it is the only cheap way to assert BOTH directions in one
    // test. What a client actually receives is pinned end to end beside it, over the real transport and the real
    // decoder: TileWorldServerShardingTests.A_viewer_never_receives_an_entity_on_another_plane for a resident, and
    // A_ghost_on_another_plane_never_reaches_the_viewers_snapshot for the border mirror.
    [Fact]
    public void The_serve_is_filtered_to_the_viewers_own_plane()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        long ground = s.SpawnPlayer(0, "a", "Ari");
        long upstairs = s.SpawnPlayer(1, "b", "Bo");
        s.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 1), TileDirection.S));
        s.Tick(Dt);

        HashSet<long> seenByGround = s.ServeInterest(0);
        Assert.Contains(ground, seenByGround);
        Assert.DoesNotContain(upstairs, seenByGround);

        HashSet<long> seenByUpstairs = s.ServeInterest(1);
        Assert.Contains(upstairs, seenByUpstairs);
        Assert.DoesNotContain(ground, seenByUpstairs);
    }

    // Equality between the two heads is the point, and on its own it is satisfied by two servers that both
    // ignored every command: default equals default. The end-state anchors below are what make that reading
    // impossible, so the test can only pass by actually running the walks it fed in.
    [Fact]
    public void Two_servers_fed_the_same_joins_and_commands_reach_the_same_state()
    {
        var hubA = new InMemoryTransportHub();
        var hubB = new InMemoryTransportHub();
        using TileWorldServer a = Server(TileMoveSimulatorTests.FlatWorld(), hubA.Server, new TileCoord(10, 10, 0));
        using TileWorldServer b = Server(TileMoveSimulatorTests.FlatWorld(), hubB.Server, new TileCoord(10, 10, 0));
        foreach (TileWorldServer s in new[] { a, b })
        {
            s.SpawnPlayer(0, "a", "Ari");
            s.SpawnPlayer(1, "b", "Bo");
            s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(18, 14, 0), TileMoveMode.Run));
            s.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(4, 4, 0), TileMoveMode.Walk));
            for (int i = 0; i < 32; i++) s.Tick(Dt);      // long enough for the slower walk to arrive too
        }

        Assert.True(a.TryGetPlayerState(0, out TileMoveState a0));
        Assert.True(b.TryGetPlayerState(0, out TileMoveState b0));
        Assert.Equal(a0, b0);
        Assert.True(a.TryGetPlayerState(1, out TileMoveState a1));
        Assert.True(b.TryGetPlayerState(1, out TileMoveState b1));
        Assert.Equal(a1, b1);

        Assert.Equal(new TileCoord(18, 14, 0), a0.Tile);
        Assert.Equal(new TileCoord(4, 4, 0), a1.Tile);
        Assert.True(a0.Route.IsIdle);
        Assert.True(a1.Route.IsIdle);
    }

    [Fact]
    public void A_walk_across_a_region_boundary_keeps_the_net_id_and_the_route()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(61, 10, 0));
        long netId = s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(68, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 20; i++) s.Tick(Dt);

        Assert.True(s.TryGetPlayerNetId(0, out long stillNetId));
        Assert.Equal(netId, stillNetId);
        Assert.True(s.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.Equal(new CellCoord(1, 0), cell.Coord);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(68, 10, 0), st.Tile);
    }

    [Fact]
    public void A_route_survives_the_handoff_mid_walk()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(62, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(70, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 6; i++) s.Tick(Dt);            // crosses x=64 inside this window

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.True(st.Tile.X >= 64);
        Assert.False(st.Route.IsIdle);
        Assert.Equal(new TileCoord(70, 10, 0), st.Route.End);
    }

    // The crossing seen from the OTHER side, which is what makes it seamless: the watcher's own cell already holds
    // the walker as a border ghost before authority moves, and holds it as a resident afterwards, with the same net
    // id and no gap in the watcher's area of interest. A despawn and respawn would read as a player blinking.
    [Fact]
    public void A_watcher_across_the_boundary_sees_the_ghost_and_then_the_resident()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(doc, hub.Server, new TileCoord(61, 10, 0));
        long walker = s.SpawnPlayer(0, "a", "Ari");
        long watcher = s.SpawnPlayer(1, "b", "Bo");
        s.SetPlayerState(1, TileMoveState.At(new TileCoord(70, 10, 0), TileDirection.S));
        s.Tick(Dt);   // the watcher crosses into cell (1, 0) and the walker is mirrored in behind it

        Assert.True(s.Host.TryGetCell(new CellCoord(1, 0), out CellSim east));
        Assert.True(s.Host.TryGetOwner(watcher, out CellSim watcherCell, out _));
        Assert.Equal(new CellCoord(1, 0), watcherCell.Coord);
        Assert.True(east.TryGetGhost(walker, out _));
        Assert.False(east.TryGetOwned(walker, out _));
        Assert.Contains(walker, s.ServeInterest(1));

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(68, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 20; i++) s.Tick(Dt);

        Assert.True(east.TryGetOwned(walker, out _));
        Assert.False(east.TryGetGhost(walker, out _));   // adopted, never a ghost and a resident at once
        Assert.Contains(walker, s.ServeInterest(1));
    }

    // A frame whose bytes a CLIENT chooses, driven through the real encoder, the real decoder, the real transport
    // and the real server. The goal's X and Z are the only decoded fields with no wire bound, deliberately: the
    // radius is measured from where the player currently stands, so only the server can judge it, and
    // TryDecodeCommand says so in its own doc. That leaves the subtraction inside GoalInRange as the one place a
    // remote peer picks both operands. A goal of int.MinValue + tileX makes it exactly int.MinValue, which is the
    // one value Math.Abs cannot negate, and the throw comes out of Admit in step 1 of the tick body and takes the
    // WHOLE tick down, for every other player on the server, not only the one who sent it.
    [Fact]
    public void A_crafted_goal_no_int_can_measure_is_refused_instead_of_killing_the_tick()
    {
        (LoopbackTransport serverEnd, LoopbackTransport clientEnd) = LoopbackTransport.CreatePair();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), serverEnd, new TileCoord(10, 10, 0));
        var client = new NetClient(clientEnd);
        for (int i = 0; i < 2; i++) { client.Poll(); s.Poll(); }   // Hello, seat, Welcome
        Assert.Equal(1, s.PlayerCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState before));
        Assert.Equal(new TileCoord(10, 10, 0), before.Tile);

        var crafted = TileCommand.WalkTo(
            new TileCoord(int.MinValue + before.Tile.X, before.Tile.Z, 0), TileMoveMode.Run);
        byte[] frame = TileProtocol.EncodeCommand(0, crafted);
        // Well formed all the way to the tick: the decoder admits it, so nothing before Admit ever sees it as odd.
        Assert.True(TileProtocol.TryDecodeCommand(frame, planeCount: 4, out int seq, out TileCommand decoded));
        Assert.Equal(0, seq);
        Assert.Equal(int.MinValue + before.Tile.X, decoded.Goal.X);

        client.Send(frame, NetChannelReliability.ReliableOrdered);
        s.Poll();
        s.Tick(Dt);

        Assert.Equal(1, s.TickCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(new TileCoord(10, 10, 0), after.Tile);
        Assert.True(after.Route.IsIdle);
        // Rewritten to Continue(cmd.Mode) exactly as any other out-of-range goal is, so the run toggle the refused
        // walk carried still applies and the client predicting the same rewrite stays in step.
        Assert.Equal(TileMoveMode.Run, after.Mode);
    }
}
