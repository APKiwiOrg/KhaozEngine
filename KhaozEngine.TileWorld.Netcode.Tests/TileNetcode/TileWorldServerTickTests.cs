using System.Collections.Generic;
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
        Assert.Equal(new TileCoord(10, 11, 0), st.Tile);
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
        Assert.Equal(new TileCoord(1, 1, 0), tilesSeen[0]);
        Assert.Equal(new TileCoord(1, 1, 0), tilesSeen[1]);
        Assert.Equal(new TileCoord(1, 2, 0), tilesSeen[2]);
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
        Assert.Equal(new TileCoord(10, 12, 0), a.Tile);
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
        Assert.Equal(new TileCoord(10, 13, 0), st.Tile);
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
            for (int i = 0; i < 12; i++) s.Tick(Dt);
        }

        Assert.True(a.TryGetPlayerState(0, out TileMoveState a0));
        Assert.True(b.TryGetPlayerState(0, out TileMoveState b0));
        Assert.Equal(a0, b0);
        Assert.True(a.TryGetPlayerState(1, out TileMoveState a1));
        Assert.True(b.TryGetPlayerState(1, out TileMoveState b1));
        Assert.Equal(a1, b1);
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
}
