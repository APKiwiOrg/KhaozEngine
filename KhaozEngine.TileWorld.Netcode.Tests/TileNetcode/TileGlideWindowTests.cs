using System;
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
    // remotes slide. Seeded rather than ticked, so the prediction layer sits fully on its current tick (its
    // inter-tick easing is at 1 and its correction offset at zero) and the two paths are asking about the same
    // instant of the same step.
    [Fact]
    public void The_local_pose_and_a_remote_pose_agree_under_the_same_window()
    {
        foreach (byte ticks in new byte[] { 0, 1, 2, 3 })
        {
            TileMoveState s = Stepping(4, ticks);
            ClientPrediction<TileMoveState, TileCommand> pred = Prediction();
            pred.Reset(s);
            Assert.Equal(Windowed.Pose(s).Position, Windowed.LocalPose(pred).Position);
        }
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

        const float Frame = 1f / 60f;
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

    static ClientPrediction<TileMoveState, TileCommand> Prediction() =>
        new(new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
                new TileStepTicks(4, 2)),
            new PredictionSettings(Tick, 64, 0.5f, 8f, 0.01f));
}
