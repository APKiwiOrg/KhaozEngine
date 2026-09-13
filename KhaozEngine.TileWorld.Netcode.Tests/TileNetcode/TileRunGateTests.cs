using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

// TileWorldServerConfig.CanRun, the server's authority behind a game's run energy. The cadences here are the
// shared harness's: walk is 4 ticks per step and run is 2, so the same route read at a walking cadence takes
// exactly twice as many ticks to cover the same ground, and every assertion below is that arithmetic.
public class TileRunGateTests
{
    const float Dt = 0.25f;

    static TileWorldServer Server(InMemoryTransportHub hub, TileCoord spawn, Func<int, bool>? canRun)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        return new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(spawn) with { CanRun = canRun },
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    // No gate is the shipped default and has to cost the player nothing: the same route, the same commands, the
    // run cadence. This is the control every other test here is read against.
    [Fact]
    public void With_no_gate_a_running_player_keeps_the_run_cadence()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0), canRun: null);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        for (int i = 1; i <= 2; i++) { s.Tick(Dt); s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); }

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(TileMoveMode.Run, st.Mode);
        Assert.Equal(2, st.StepTotal);
        Assert.Equal(new TileCoord(10, 12, 0), st.Tile);
        Assert.Equal(new Vector2(10f, 11f), st.Position);
        Assert.Equal(0L, s.GatedRunCount);
    }

    // A closed gate steps the player at a walk however loudly the client keeps sending Run, which is the whole
    // point: a patched client cannot run on an empty energy bar. Four ticks of walking cover the ground two ticks
    // of running covered above, and the state's own Mode reads Walk, so the snapshot tells the same story.
    [Fact]
    public void A_closed_gate_steps_a_running_client_at_the_walk_cadence()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0), canRun: _ => false);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        for (int i = 1; i <= 4; i++) { s.Tick(Dt); s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); }

        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(TileMoveMode.Walk, st.Mode);
        Assert.Equal(4, st.StepTotal);
        Assert.Equal(new TileCoord(10, 12, 0), st.Tile);
        Assert.Equal(new Vector2(10f, 11f), st.Position);
        // Once per command the gate rewrote, not once per player and not once per tick: four commands arrived
        // carrying Run and all four were downgraded.
        Assert.Equal(4L, s.GatedRunCount);
        // The route itself survives the rewrite. Only the mode is touched, so the click still walks.
        Assert.Equal(new TileCoord(10, 20, 0), st.Route.End);
    }

    // The gate lands at the start of the NEXT step, exactly as a client's own toggle does, because the simulator
    // stamps a step's cadence as it commits. A gate that cut the step already under way would stretch a glide the
    // client had already predicted at two ticks.
    [Fact]
    public void A_gate_that_closes_mid_step_lets_that_step_finish_at_its_own_cadence()
    {
        bool allowed = true;
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0), canRun: _ => allowed);
        s.SpawnPlayer(0, "a", "Ari");

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        s.Tick(Dt);                                  // one tick into a two-tick run step
        Assert.True(s.TryGetPlayerState(0, out TileMoveState mid));
        Assert.Equal(2, mid.StepTotal);
        Assert.Equal(1, mid.StepTicks);

        allowed = false;
        s.Enqueue(0, 1, TileCommand.Continue(TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState landed));
        // The step under way still filled on its second tick rather than being stretched to a walk's four, and the
        // step committed as the body landed is the one that carries the new cadence.
        Assert.Equal(new Vector2(10f, 11f), landed.Position);
        Assert.Equal(new TileCoord(10, 12, 0), landed.Tile);
        Assert.Equal(TileMoveMode.Walk, landed.Mode);
        Assert.Equal(4, landed.StepTotal);

        for (int i = 2; i <= 5; i++) { s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); s.Tick(Dt); }
        Assert.True(s.TryGetPlayerState(0, out TileMoveState walked));
        // Four ticks for the next tile, not two.
        Assert.Equal(new Vector2(10f, 12f), walked.Position);
        Assert.Equal(new TileCoord(10, 13, 0), walked.Tile);
    }

    // And it reopens the same way round. Nothing has to be re-clicked: the client is still sending Run, so the
    // first step committed after the gate returns true is a run step again.
    [Fact]
    public void A_gate_that_reopens_puts_the_next_step_back_on_the_run_cadence()
    {
        bool allowed = false;
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0), canRun: _ => allowed);
        s.SpawnPlayer(0, "a", "Ari");

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        for (int i = 1; i <= 4; i++) { s.Tick(Dt); s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); }
        Assert.Equal(4L, s.GatedRunCount);

        allowed = true;
        // The step standing when the gate reopened was stamped at a walk, so it finishes at four ticks and the one
        // committed as the body lands is the run step.
        for (int i = 5; i <= 8; i++) { s.Tick(Dt); s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); }
        Assert.True(s.TryGetPlayerState(0, out TileMoveState reopened));
        Assert.Equal(TileMoveMode.Run, reopened.Mode);
        Assert.Equal(2, reopened.StepTotal);
        Assert.Equal(new TileCoord(10, 13, 0), reopened.Tile);

        for (int i = 9; i <= 10; i++) { s.Tick(Dt); s.Enqueue(0, i, TileCommand.Continue(TileMoveMode.Run)); }
        Assert.True(s.TryGetPlayerState(0, out TileMoveState running));
        Assert.Equal(new TileCoord(10, 14, 0), running.Tile);
        Assert.Equal(new Vector2(10f, 13f), running.Position);
        // Nothing more was downgraded once the gate opened.
        Assert.Equal(4L, s.GatedRunCount);
    }

    // The case the gate has to run LAST for. A tick with no command from the client is admitted as Continue at the
    // player's CURRENT mode, which is Run for anybody who was already running when the gate closed. A gate applied
    // inside the admission switch would never see that command, and a player whose packets stopped arriving would
    // keep running on an empty bar for as long as the starvation lasted.
    [Fact]
    public void A_starved_tick_at_Run_is_admitted_at_Walk_once_the_gate_closes()
    {
        bool allowed = true;
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0), canRun: _ => allowed);
        s.SpawnPlayer(0, "a", "Ari");

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState running));
        Assert.Equal(TileMoveMode.Run, running.Mode);
        Assert.Equal(0L, s.GatedRunCount);

        allowed = false;
        s.Tick(Dt);                                  // nothing enqueued: the starvation neutral, at Run
        Assert.True(s.TryGetPlayerState(0, out TileMoveState gated));
        Assert.Equal(TileMoveMode.Walk, gated.Mode);
        Assert.Equal(4, gated.StepTotal);
        Assert.Equal(1L, s.GatedRunCount);

        // The neutral is Continue(Walk) from here, so there is nothing left to downgrade and the counter holds.
        s.Tick(Dt);
        Assert.Equal(1L, s.GatedRunCount);
    }

    // The gate is asked about a SLOT, and it has to be the slot whose command is being admitted or a game reads
    // the wrong player's energy. Asked only about a command that carries Run, too, so a walking world never pays
    // for the callback at all.
    [Fact]
    public void The_gate_is_asked_about_the_commanding_players_own_slot()
    {
        var asked = new List<int>();
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0),
            canRun: slot => { asked.Add(slot); return true; });
        s.SpawnPlayer(3, "a", "Ari");

        s.Enqueue(3, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.Empty(asked);                         // a walking command never reaches the gate

        for (int i = 1; i <= 3; i++) { s.Enqueue(3, i, TileCommand.Continue(TileMoveMode.Run)); s.Tick(Dt); }
        Assert.Equal(new[] { 3, 3, 3 }, asked);
    }

    // The callback is the GAME's, so a throw out of it comes out of the tick rather than being swallowed into a
    // cadence nobody chose. Stated as a test because the alternative (catch and allow, or catch and refuse) is the
    // sort of kindness that hides a broken energy rule for a whole session.
    [Fact]
    public void A_throwing_gate_takes_the_tick_down_rather_than_being_swallowed()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, new TileCoord(10, 10, 0),
            canRun: _ => throw new InvalidOperationException("energy"));
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));

        InvalidOperationException raised = Assert.Throws<InvalidOperationException>(() => s.Tick(Dt));
        Assert.Equal("energy", raised.Message);
    }
}
