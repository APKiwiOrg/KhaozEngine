using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileSteerSimulatorTests
{
    const float Dt = TileMoveSimulatorTests.Dt;
    static readonly TileCoord Spawn = new(5, 5, 0);

    static TileMoveSimulator Sim(TileWorldDocument? doc = null) =>
        new(TileMoveSimulatorTests.Bake(doc ?? TileMoveSimulatorTests.FlatWorld()), TileMoveSimulatorTests.Ticks);

    static TileMoveState Standing() => TileMoveState.At(Spawn, TileDirection.S);

    [Theory]
    [InlineData(TileMoveMode.Walk)]
    [InlineData(TileMoveMode.Run)]
    public void A_steered_line_commits_tiles_on_the_same_ticks_as_a_routed_line(TileMoveMode mode)
    {
        TileMoveSimulator sim = Sim();
        TileMoveState steered = Standing(), routed = Standing();
        for (int i = 0; i < 24; i++)
        {
            steered = sim.Step(steered, TileCommand.Steer(TileDirection.N, mode), Dt);
            routed = sim.Step(routed,
                i == 0 ? TileCommand.WalkTo(new TileCoord(5, 18, 0), mode) : TileCommand.Continue(mode), Dt);
            Assert.Equal(routed.Tile, steered.Tile);
            Assert.Equal(routed.StepFrom, steered.StepFrom);
            Assert.Equal(routed.StepTicks, steered.StepTicks);
            Assert.Equal(routed.StepTotal, steered.StepTotal);
        }
        // Goal (5, 18) is exactly the 13 tiles a RUN covers in 24 ticks, so the routed line never stops early and
        // the two are compared on every one of them. Thirteen rather than twelve because the tick that carries the
        // command commits its step immediately, so only the twelve after the first cost two ticks each. A walk pays
        // four ticks a step instead, so the same 24 ticks buy 7 tiles and leave the route live.
        Assert.Equal(mode == TileMoveMode.Run ? new TileCoord(5, 18, 0) : new TileCoord(5, 12, 0), routed.Tile);
        Assert.Equal(mode == TileMoveMode.Run, routed.Route.IsIdle);
    }

    [Fact]
    public void Releasing_lands_the_step_in_flight_and_starts_nothing()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 5, 0), s.Tile);
        for (int i = 0; i < 8; i++) s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 5, 0), s.Tile);
        Assert.False(s.IsStepping);
    }

    [Fact]
    public void A_mid_step_steer_does_not_restart_or_redirect_the_step_in_flight()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        s = sim.Step(s, TileCommand.Steer(TileDirection.W, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 5, 0), s.Tile);
        Assert.Equal(2, s.StepTicks);
    }

    [Fact]
    public void The_direction_held_on_the_landing_tick_is_the_next_step()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = Standing();
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        s = sim.Step(s, TileCommand.Steer(TileDirection.N, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 6, 0), s.Tile);
        Assert.Equal(new TileCoord(6, 5, 0), s.StepFrom);
        Assert.Equal(0, s.StepTicks);
    }

    [Fact]
    public void A_blocked_steer_stands_and_faces_the_held_direction()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("tree", 5, 6, 0, 0);
        TileMoveSimulator sim = Sim(doc);
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.N, TileMoveMode.Walk), Dt);
        Assert.Equal(Spawn, s.Tile);
        Assert.False(s.IsStepping);
        Assert.Equal(TileDirection.N, s.Facing);
    }

    [Fact]
    public void A_blocked_diagonal_slides_and_faces_the_step_it_took()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("tree", 6, 5, 0, 0);
        doc.AddObject("tree", 6, 6, 0, 0);
        TileMoveSimulator sim = Sim(doc);
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.NE, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(5, 6, 0), s.Tile);
        Assert.Equal(TileDirection.N, s.Facing);
    }

    [Fact]
    public void Steering_replaces_a_route_a_pending_interaction_and_a_fight()
    {
        // The combat seam is WIRED and its target resolves on the player's own plane, out of reach and reachable,
        // which is the only shape that makes the fight assertion say anything: with no seam the follow clears every
        // lock by itself and the last line would pass however the steer behaved. Out of reach because a lock the
        // follow is happily chasing is exactly the one a steer has to break.
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
            TileMoveSimulatorTests.Ticks, combatTargets: new ResolvingTarget(11, new TileRect(12, 5, 1, 1)));
        TileMoveState s = sim.Step(Standing(), TileCommand.WalkTo(new TileCoord(20, 20, 0), TileMoveMode.Walk), Dt);
        s.InteractTarget = 9;
        s.CombatTarget = 11;
        s = sim.Step(s, TileCommand.Steer(TileDirection.W, TileMoveMode.Walk), Dt);
        Assert.Equal(0, s.CombatTarget);
        Assert.Equal(0, s.InteractTarget);
        Assert.Equal(TileInteractionDomain.AuthoredObject, s.InteractDomain);
        // Last, because a lock left behind would have the follow rebuild a route on this very tick and the fight
        // assertion above is the one that should name that fault.
        Assert.True(s.Route.IsIdle);
    }

    [Fact]
    public void A_click_after_steering_replaces_it()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        s = sim.Step(s, TileCommand.WalkTo(new TileCoord(6, 9, 0), TileMoveMode.Walk), Dt);
        Assert.False(s.Route.IsIdle);
        Assert.Equal(new TileCoord(6, 9, 0), s.Route.End);
    }

    [Fact]
    public void A_mode_change_lands_at_the_next_step_start()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = sim.Step(Standing(), TileCommand.Steer(TileDirection.N, TileMoveMode.Walk), Dt);
        Assert.Equal(TileMoveSimulatorTests.Ticks.Walk, s.StepTotal);
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Steer(TileDirection.N, TileMoveMode.Run), Dt);
        Assert.Equal(TileMoveSimulatorTests.Ticks.Run, s.StepTotal);
    }

    [Fact]
    public void A_command_lost_on_the_landing_tick_costs_one_standing_tick_and_nothing_else()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = Standing();
        // The first three ticks of a walking step, the third of which leaves it one tick short of landing.
        for (int i = 0; i < 3; i++) s = sim.Step(s, TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 5, 0), s.Tile);
        Assert.Equal(3, s.StepTicks);

        // The steer that would have chained the next step is the one that went missing, so the landing tick reads as
        // a Continue: the body lands on the tile it already owned and starts nothing behind it.
        s = sim.Step(s, TileCommand.Continue(TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(6, 5, 0), s.Tile);
        Assert.False(s.IsStepping);
        Assert.Equal(0, s.StepTicks);

        // And exactly one tick is lost. The next steer comes in through the STANDING door, which spends its own tick
        // on the new step rather than on a glide, so it reads one tick in the moment it commits.
        s = sim.Step(s, TileCommand.Steer(TileDirection.E, TileMoveMode.Walk), Dt);
        Assert.Equal(new TileCoord(7, 5, 0), s.Tile);
        Assert.Equal(1, s.StepTicks);
        Assert.Equal(TileMoveSimulatorTests.Ticks.Walk, s.StepTotal);
    }

    [Fact]
    public void A_refused_steer_leaves_the_tick_as_though_no_command_arrived()
    {
        TileMoveSimulator sim = Sim();
        TileMoveState s = sim.Step(Standing(), TileCommand.WalkTo(new TileCoord(5, 10, 0), TileMoveMode.Walk), Dt);
        TileRoute route = s.Route;
        Assert.Equal(1, s.StepTicks);

        // A direction outside the eight is refused by Accepts, so the case never matches and its MODE is dropped
        // with the rest of it. A refusal that applied the mode would put this walking body on a running cadence at
        // the next step, which is a divergence the client would have to be snapped out of.
        s = sim.Step(s, new TileCommand(TileCommandKind.Steer, default, TileMoveMode.Run, 99L), Dt);
        Assert.Equal(TileMoveMode.Walk, s.Mode);
        Assert.Equal(route, s.Route);
        Assert.Equal(new TileCoord(5, 6, 0), s.Tile);
        Assert.Equal(2, s.StepTicks);
        Assert.Equal(TileMoveSimulatorTests.Ticks.Walk, s.StepTotal);
    }

    [Fact]
    public void Two_instances_steering_the_same_inputs_stay_identical()
    {
        TileMoveSimulator a = Sim(), b = Sim();
        TileMoveState sa = Standing(), sb = Standing();
        TileDirection[] held = { TileDirection.N, TileDirection.NE, TileDirection.E, TileDirection.SW };
        for (int i = 0; i < 48; i++)
        {
            TileCommand c = i % 7 == 6
                ? TileCommand.Continue(TileMoveMode.Run)
                : TileCommand.Steer(held[i / 12], i < 24 ? TileMoveMode.Walk : TileMoveMode.Run);
            sa = a.Step(sa, c, Dt);
            sb = b.Step(sb, c, Dt);
            Assert.Equal(sa, sb);
        }
    }

    // One entity that always resolves, at a fixed 1x1 rect on plane 0, which is what the server's own entity space
    // answers for an actor standing still.
    sealed class ResolvingTarget(long id, TileRect rect) : ITileTargets
    {
        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            footprint = rect;
            plane = 0;
            return target == id;
        }
    }
}
