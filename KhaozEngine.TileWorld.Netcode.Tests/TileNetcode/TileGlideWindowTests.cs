using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The glide window: how long the DRAWN body may lag the tile the simulation already committed it to. The step
/// itself is untouched by all of this (the simulator is not called once in here), which is the point: the window is
/// read on the way to a view and written to nothing.
/// </summary>
public class TileGlideWindowTests
{
    const float Tick = 0.25f;
    const float PlaneMetres = 3f;
    const float Frame = 1f / 60f;

    // A quarter-second window against a quarter-second tick: a quarter of a four-tick walking step, and half of a
    // two-tick running one.
    static readonly TileGlideWindow QuarterOfAWalk = new(seconds: 0.25f, tickSeconds: Tick);
    static readonly TilePresenter Windowed = new(tileSize: 1f, planeHeight: PlaneMetres, QuarterOfAWalk);
    static readonly TilePresenter FullStep = new(tileSize: 1f, planeHeight: PlaneMetres);

    // One north step: out of tile (0, 0) and into tile (0, 1), which the state already names.
    static TileMoveState Stepping(byte stepTotal, byte stepTicks = 0)
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N);
        s.StepFrom = new TileCoord(0, 0, 0);
        s.StepTotal = stepTotal;
        s.StepTicks = stepTicks;
        return s;
    }

    // The tile-space z the pose sits at, so a test can talk about how far along the step the body is drawn rather
    // than about world metres. The step runs from z 0.5 (the centre of the departed tile) to z 1.5.
    static float DrawnZ(TilePose pose) => TileWorldSpace.TileZ(pose.Position.Z, 1f);

    static float DrawnFraction(TilePose pose) => DrawnZ(pose) - 0.5f;

    // THE remap, pinned at the three fractions that say what it does. A quarter-step window means the body has
    // covered the whole step by the time a quarter of it has elapsed, and then waits on its tile.
    [Fact]
    public void A_quarter_step_window_lands_the_body_on_its_tile_a_quarter_of_the_way_through_the_step()
    {
        // Fraction 0.25 of a four-tick step: one whole tick in, and already AT the committed tile.
        Assert.Equal(1.5f, DrawnZ(Windowed.Pose(Stepping(4, stepTicks: 1))), 5);
        // Fraction 0.1: four tenths of the way along, because a tenth is four tenths of the quarter-step window.
        Assert.Equal(0.9f, DrawnZ(Windowed.Pose(Stepping(4), extraTicks: 0.4f)), 5);
        // Fraction 0: the tick the step commits, and the body has not left yet.
        Assert.Equal(0.5f, DrawnZ(Windowed.Pose(Stepping(4))), 5);
        // And it STAYS on the tile for the rest of the step rather than overshooting it.
        Assert.Equal(1.5f, DrawnZ(Windowed.Pose(Stepping(4, stepTicks: 3))), 5);
    }

    // The reason the window is in SECONDS. A run is a shorter step than a walk, so a window measured as a share of
    // the step would make the walking catch-up take twice as long as the running one and the two would not read as
    // the same game. Measured in seconds they are the same curve against the wall clock.
    [Fact]
    public void A_walk_and_a_run_catch_up_at_the_same_wall_clock_rate()
    {
        foreach (float seconds in new[] { 0.05f, 0.1f, 0.2f, 0.25f, 0.4f })
        {
            float ticks = seconds / Tick;
            float walk = DrawnFraction(Windowed.Pose(Stepping(4), ticks));
            float run = DrawnFraction(Windowed.Pose(Stepping(2), ticks));
            Assert.Equal(walk, run, 5);
            // Arrived by the window's own duration, and not before it.
            Assert.Equal(seconds >= 0.25f ? 1f : seconds * 4f, walk, 5);
        }

        // The contrast that makes the assertion above mean something: WITHOUT a window the same instant finds the
        // run twice as far into its step as the walk, because the step is half as long.
        Assert.Equal(0.25f, DrawnFraction(FullStep.Pose(Stepping(4), 1f)), 5);
        Assert.Equal(0.5f, DrawnFraction(FullStep.Pose(Stepping(2), 1f)), 5);
    }

    // The default is invisible: a presenter that was handed no window draws the same bytes the presenter drew before
    // windows existed. The expected value is the OLD arithmetic restated here rather than read back off the
    // presenter, so this fails if the remap ever touches the default path.
    [Fact]
    public void The_default_window_draws_the_full_step_glide_byte_for_byte()
    {
        foreach (byte total in new byte[] { 1, 2, 4, 7 })
            foreach (byte ticks in new byte[] { 0, 1, 3 })
                foreach (float extra in new[] { -1f, 0f, 0.37f, 1f, 9f })
                {
                    TileMoveState s = Stepping(total, ticks);
                    float f = Math.Clamp((s.StepTicks + Math.Max(0f, extra)) / s.StepTotal, 0f, 1f);
                    float x = s.StepFrom.X + ((float)s.Tile.X - s.StepFrom.X) * f;
                    float z = s.StepFrom.Z + ((float)s.Tile.Z - s.StepFrom.Z) * f;
                    Vector3 expected = TileWorldSpace.ToWorld(x + 0.5f, s.Tile.Plane * PlaneMetres, z + 0.5f, 1f);
                    Assert.Equal(expected, FullStep.Pose(s, extra).Position);
                }
    }

    // The same pin on the local path, which on the default window is still the prediction layer's rendered override
    // drawn verbatim with nothing rebuilt.
    [Fact]
    public void The_default_window_draws_the_local_players_rendered_override_byte_for_byte()
    {
        ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
        pred.Reset(Stepping(4, stepTicks: 2));
        pred.Predict(TileCommand.Continue(TileMoveMode.Walk));
        pred.AdvancePresentation(0.1f);

        TileMoveState r = pred.RenderedState;
        Vector3 expected = TileWorldSpace.ToWorld(r.RenderPosition.X + 0.5f, r.RenderVertical * PlaneMetres,
            r.RenderPosition.Y + 0.5f, 1f);
        Assert.Equal(expected, FullStep.LocalPose(pred).Position);
    }

    // Local and remote read the SAME window off the SAME presenter, so the local player cannot snap while the
    // remotes slide. Asserted where it can actually FAIL: the layer is left genuinely mid-tick, at every phase from
    // the instant of a command tick to the end of one, rather than parked at the phase of 1 a bare Reset leaves it
    // at. Parked there the local reconstruction collapses to StepTicks / StepTotal, which is the same expression the
    // remote path evaluates at zero extra ticks, and the two agree for arithmetic reasons rather than for the reason
    // the test is about.
    [Fact]
    public void The_local_pose_and_a_remote_pose_agree_under_the_same_window()
    {
        var phases = new List<float>();
        foreach (byte ticks in new byte[] { 0, 1, 2, 3 })
            foreach (float phase in new[] { 0f, 0.2f, 0.5f, 0.9f, 1f })
            {
                ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
                pred.Reset(Stepping(4, ticks));
                // A real tick, so the layer's phase runs from 0 again, and then part of a tick of frames on top.
                pred.Predict(TileCommand.Continue(TileMoveMode.Walk));
                pred.AdvancePresentation(phase * Tick);
                phases.Add(pred.InterTickFraction);

                // The same state and the same sub-tick offset, down the two paths.
                TileMoveState s = pred.PredictedState;
                Assert.Equal(Windowed.Pose(s, pred.InterTickFraction).Position, Windowed.LocalPose(pred).Position);
            }

        // The phase really did vary, or the agreement above is the vacuous one.
        Assert.Equal(new[] { 0f, 0.2f, 0.5f, 0.9f, 1f }, phases.Distinct().Order());
    }

    // Important 1 of the branch review: a window inside ONE TICK of the step's own duration. The local fraction used
    // to be measured a tick behind the state, the way the prediction layer's own easing is, which meant it topped out
    // at (StepTotal - 1) / StepTotal and never reached 1. The body stopped short of its committed tile and then
    // jumped forward onto the next step's StepFrom at every commit.
    //
    // The band is not exotic. At the engine's own TileStepTicks(4, 2) and a quarter-second tick the RUN sits in it for
    // every window between 0.25 s and 0.5 s, so a game tuning by feel lands there by walking into it.
    [Theory]
    // Run cadence, a 0.45 s window on a two-tick (0.5 s) step: 0.481 tiles of forward pop per commit, measured.
    [InlineData(0.45f, 4, 2, TileMoveMode.Run)]
    // One-tick cadence, where (StepTotal - 1) / StepTotal is ZERO, so any window shorter than the step removed the
    // local glide entirely and teleported the body a whole tile every tick: 1.000 tiles, measured.
    [InlineData(0.20f, 1, 1, TileMoveMode.Walk)]
    [InlineData(0.05f, 1, 1, TileMoveMode.Walk)]
    public void A_window_within_one_tick_of_the_step_glides_the_local_body_instead_of_popping_it(
        float window, int walk, int run, TileMoveMode mode)
    {
        (float worst, int commits) = WalkNorth(Tick, new TileStepTicks((byte)walk, (byte)run), mode, window);

        // The walk really did commit tiles, or there is no commit for a pop to happen at.
        Assert.True(commits >= 4, $"expected several step commits, saw {commits}");
        float bound = Bound(window);
        Assert.True(worst < bound,
            $"a frame moved the body {worst} tiles against a bound of {bound}, which is a pop rather than a glide");
    }

    // Minor 2 of the review, which the fix above is what closes. FractionOf divides the window by the float product
    // StepTotal * TickSeconds, and a game that writes the whole step as a decimal ("three 35 ms ticks is 105 ms")
    // lands a hair UNDER that product rather than on it, so the window takes the windowed path instead of the
    // untouched full-step one. That used to put it at the very worst point of the band above.
    [Fact]
    public void A_window_a_float_hair_under_the_whole_step_still_lands_the_body_on_its_tile()
    {
        var edge = new TileGlideWindow(0.105f, 0.035f);
        Assert.Equal(0.99999994f, edge.FractionOf(3));
        Assert.False(edge.CoversWholeStep(3));

        (float worst, int commits) = WalkNorth(0.035f, new TileStepTicks(3, 3), TileMoveMode.Walk, 0.105f);
        Assert.True(commits >= 4, $"expected several step commits, saw {commits}");
        float bound = Bound(0.105f);
        Assert.True(worst < bound,
            $"a frame moved the body {worst} tiles against a bound of {bound}, which is a pop rather than a glide");
    }

    // The window remaps the STEP and nothing else. A decaying correction offset rides through it untouched, which
    // is what stops a misprediction from cutting instead of easing: put the whole rendered position through the
    // multiplier instead and the offset is clamped away with the step it was riding on.
    [Fact]
    public void A_windowed_local_pose_carries_the_correction_offset_through_unchanged()
    {
        // A quiet resume onto a basis a quarter tile back re-anchors that quarter tile into the decaying offset
        // (see ClientPrediction.Reseed), which is the shortest way to a non-zero one.
        ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
        pred.Reset(Stepping(4, stepTicks: 2));
        pred.Reseed(Stepping(4, stepTicks: 1));
        Assert.NotEqual(Vector2.Zero, pred.RenderOffset);

        // Fraction 0.25 of the step, so the window has the body on its committed tile: z 1.5, plus the offset the
        // resume left, which is a quarter tile further north (world -z).
        float offsetZ = pred.RenderOffset.Y;
        Assert.Equal(0.25f, offsetZ, 5);
        Assert.Equal(1.5f + offsetZ, DrawnZ(Windowed.LocalPose(pred)), 5);
    }

    // A teleport leaves StepFrom on Tile: there is no step to glide and therefore nothing for a window to remap.
    // It cuts under the tiniest window exactly as it cuts under none.
    [Fact]
    public void A_teleport_still_cuts_under_a_tiny_window()
    {
        var tiny = new TilePresenter(1f, PlaneMetres, new TileGlideWindow(0.001f, Tick));
        TileMoveState placed = TileMoveState.At(new TileCoord(9, 4, 1), TileDirection.E);
        placed.Epoch = 7;
        Vector3 centre = TileWorldSpace.ToWorld(9.5f, PlaneMetres, 4.5f, 1f);

        Assert.Equal(centre, tiny.Pose(placed).Position);
        Assert.Equal(centre, tiny.Pose(placed, extraTicks: 12f).Position);

        ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
        pred.Reset(placed);
        Assert.Equal(centre, tiny.LocalPose(pred).Position);
    }

    // Zero seconds is the strictest reading of the invariant: the body is on the tile the tick the step commits, so
    // the picture never disagrees with the rules at all.
    [Fact]
    public void A_zero_window_puts_the_body_on_its_tile_the_tick_the_step_commits()
    {
        var snap = new TilePresenter(1f, PlaneMetres, new TileGlideWindow(0f, Tick));
        foreach (byte ticks in new byte[] { 0, 1, 3 })
            Assert.Equal(1.5f, DrawnZ(snap.Pose(Stepping(4, ticks))), 5);

        ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
        pred.Reset(Stepping(4));
        Assert.Equal(1.5f, DrawnZ(snap.LocalPose(pred)), 5);
    }

    // At or above the step's own duration the window has nothing left to bound, and it degrades to the untouched
    // full-step glide rather than to something almost like it.
    [Fact]
    public void A_window_at_or_above_the_step_duration_is_the_full_step_glide()
    {
        // A four-tick step at a quarter-second tick is one second long.
        foreach (float seconds in new[] { 1f, 2.5f, float.PositiveInfinity })
        {
            var wide = new TilePresenter(1f, PlaneMetres, new TileGlideWindow(seconds, Tick));
            foreach (byte ticks in new byte[] { 0, 1, 3 })
                Assert.Equal(FullStep.Pose(Stepping(4, ticks)).Position, wide.Pose(Stepping(4, ticks)).Position);
        }
    }

    // The unconfigured value is the full step rather than an accidental zero-second snap, which is the difference
    // between a knob that is invisible until a game reaches for it and one that changes every consumer's picture on
    // the version they adopt.
    [Fact]
    public void The_default_window_value_covers_every_step()
    {
        Assert.Equal(TileGlideWindow.WholeStep, default(TileGlideWindow));
        foreach (byte total in new byte[] { 1, 2, 4, 255 })
            Assert.True(TileGlideWindow.WholeStep.CoversWholeStep(total));
        Assert.False(QuarterOfAWalk.CoversWholeStep(4));
        Assert.True(new TileGlideWindow(1f, Tick).CoversWholeStep(4));
    }

    // A window has to be a duration, so the two ways of failing to name one are refused at construction rather than
    // read as "no window": a caller that passed either meant to configure the feature.
    [Fact]
    public void A_window_refuses_a_negative_duration_and_a_tick_that_is_not_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileGlideWindow(-0.1f, Tick));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileGlideWindow(float.NaN, Tick));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileGlideWindow(0.25f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileGlideWindow(0.25f, -1f));
    }

    // The client composes the game's one number with its own tick length, and stamps it on the placeholder presenter
    // so a head that never loads a document still draws the window its config asked for.
    [Fact]
    public void A_client_composes_its_config_window_with_its_tick_and_stamps_the_placeholder_presenter()
    {
        using var client = new TileWorldClient(new InMemoryTransportHub().CreateClient(),
            new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = new TileStepTicks(4, 2),
                GlideWindowSeconds = 0.25f,
            },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));

        Assert.Equal(QuarterOfAWalk, client.Glide);
        Assert.Equal(QuarterOfAWalk, client.Presenter.Glide);

        using var plain = new TileWorldClient(new InMemoryTransportHub().CreateClient(),
            new TileWorldClientConfig { TickSeconds = Tick, StepTicks = new TileStepTicks(4, 2) },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
        Assert.True(plain.Glide.CoversWholeStep(4));
    }

    // The seam a windowed local pose is easiest to break at: the tick a step COMMITS, where the prediction layer is
    // still easing out of the step before it while the state already names the next one. The fraction is rebuilt
    // from the state rather than measured off the drawn point precisely so this stays continuous, and a real walk
    // with real turns in it is the only honest way to say so.
    //
    // The bound is the window's OWN speed. A window crosses the whole step in its seconds, and the longest step on
    // this route is a diagonal at sqrt(2) tiles, so a frame of a sixtieth of a second moves the body at most
    // sqrt(2) / (0.25 * 60), about 0.094 tiles. The bound sits just above that. A corner that jumped would move it
    // about a quarter of a tile in a single frame, well clear of the bound, which is what this is looking for.
    [Fact]
    public void A_windowed_local_pose_stays_continuous_across_a_step_commit_and_a_turn()
    {
        ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
        pred.Reset(TileMoveState.At(new TileCoord(2, 2, 0), TileDirection.N));
        pred.Predict(TileCommand.WalkTo(new TileCoord(6, 8, 0), TileMoveMode.Walk));

        int turns = 0, commits = 0;
        TileDirection facing = pred.PredictedState.Facing;
        TileCoord tile = pred.PredictedState.Tile;
        Vector3 previous = Windowed.LocalPose(pred).Position;
        float worst = 0f;
        // Sixteen ticks of walking, sampled every frame, with the command tick landing between frames the way a real
        // head's does rather than on one.
        for (int frame = 0; frame < (int)(16 * Tick / Frame); frame++)
        {
            pred.AdvancePresentation(Frame);
            if ((frame + 1) % 15 == 0) pred.Predict(TileCommand.Continue(TileMoveMode.Walk));
            Vector3 now = Windowed.LocalPose(pred).Position;
            worst = MathF.Max(worst, (now - previous).Length());
            previous = now;
            if (!pred.PredictedState.Tile.Equals(tile)) { commits++; tile = pred.PredictedState.Tile; }
            if (pred.PredictedState.Facing != facing) { turns++; facing = pred.PredictedState.Facing; }
        }

        // The walk really did commit tiles and really did turn, or the continuity claim above is vacuous.
        Assert.True(commits >= 4, $"expected several step commits, saw {commits}");
        Assert.True(turns >= 1, $"expected at least one turn, saw {turns}");
        Assert.True(worst < 0.11f, $"a frame moved the body {worst} tiles, which is a jump rather than a glide");
    }

    // The most a single frame may move a windowed body along a STRAIGHT route, which is the window's own crossing
    // rate: the window covers one whole tile in its seconds, so a frame covers Frame / window of a tile. Two frames'
    // worth, not one, because a command tick fired off an accumulator lands on the first frame at or past the tick
    // rather than exactly on it: the state advances a whole tick while the rendered phase was a fraction of a frame
    // short of one, and that remainder is drawn in the same frame. A POP is nothing like either number. The ones this
    // bound was written against were a third to a whole tile, five to fifteen times over it.
    static float Bound(float window) => 2f * Frame / window;

    static ClientPrediction<TileMoveState, TileCommand> Prediction() => Prediction(Tick, new TileStepTicks(4, 2));

    static ClientPrediction<TileMoveState, TileCommand> Prediction(float tick, TileStepTicks steps) =>
        new(new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), steps),
            new PredictionSettings(tick, 64, 0.5f, 8f, 0.01f));

    // Walks a windowed local body due north for twenty-four command ticks, sampled every frame, and reports the worst
    // single-frame movement and the number of tiles it committed. STRAIGHT on purpose, so a step is exactly one tile
    // and the caller's bound is the window's own crossing rate with nothing to allow for a diagonal.
    //
    // The command tick is fired off an accumulator rather than off a frame count, which is what a real head's tick
    // host does: the frame rate and the tick rate are unrelated numbers, and a cadence they do not divide evenly is
    // exactly the case a fixed "every N frames" loop cannot reach.
    static (float Worst, int Commits) WalkNorth(float tick, TileStepTicks steps, TileMoveMode mode, float window)
    {
        ClientPrediction<TileMoveState, TileCommand> pred = Prediction(tick, steps);
        var presenter = new TilePresenter(1f, PlaneMetres, new TileGlideWindow(window, tick));
        pred.Reset(TileMoveState.At(new TileCoord(2, 2, 0), TileDirection.N));
        pred.Predict(TileCommand.WalkTo(new TileCoord(2, 40, 0), mode));

        Vector3 previous = presenter.LocalPose(pred).Position;
        TileCoord tile = pred.PredictedState.Tile;
        float worst = 0f, accumulated = 0f;
        int commits = 0;
        for (int frame = 0; frame < (int)(24 * tick / Frame); frame++)
        {
            pred.AdvancePresentation(Frame);
            accumulated += Frame;
            if (accumulated >= tick) { accumulated -= tick; pred.Predict(TileCommand.Continue(mode)); }
            Vector3 now = presenter.LocalPose(pred).Position;
            worst = MathF.Max(worst, (now - previous).Length());
            previous = now;
            if (!pred.PredictedState.Tile.Equals(tile)) { commits++; tile = pred.PredictedState.Tile; }
        }
        return (worst, commits);
    }
}
