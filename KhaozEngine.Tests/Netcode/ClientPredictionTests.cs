using KhaozEngine.Netcode;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ClientPredictionTests
{
    // Command = a velocity (units/sec). Deterministic: position += command * dt.
    private readonly record struct FakeState(Vector2 Position) : IPredictedState<FakeState>
    {
        public FakeState WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class MoveSimulator : ITickSimulator<FakeState, Vector2>
    {
        public FakeState Step(in FakeState state, in Vector2 command, float dt)
            => state.WithPosition(state.Position + command * dt);
    }

    // A state with a real vertical axis, to exercise the 3D render smoothing (jumps/falls) generically - no
    // Ruinborne/NetWorld dependency. Planar = XZ ground plane (gates reconcile error), Height = vertical axis.
    private readonly record struct V3State(Vector2 Planar, float Height) : IPredictedState<V3State>
    {
        Vector2 IPredictedState<V3State>.Position => Planar;
        float IPredictedState<V3State>.Vertical => Height;
        public V3State WithPosition(Vector2 position) => this with { Planar = position };
        V3State IPredictedState<V3State>.WithRenderState(Vector2 position, float vertical)
            => this with { Planar = position, Height = vertical };
    }

    private readonly record struct V3Cmd(Vector2 Planar, float Vert);

    private sealed class V3Simulator : ITickSimulator<V3State, V3Cmd>
    {
        public V3State Step(in V3State state, in V3Cmd command, float dt)
            => new(state.Planar + command.Planar * dt, state.Height + command.Vert * dt);
    }

    private static ClientPrediction<FakeState, Vector2> NewPrediction(PredictionSettings? settings = null)
    {
        var p = new ClientPrediction<FakeState, Vector2>(new MoveSimulator(), settings);
        p.Reset(new FakeState(Vector2.Zero));
        return p;
    }

    private static ClientPrediction<V3State, V3Cmd> NewV3(PredictionSettings? settings = null)
    {
        var p = new ClientPrediction<V3State, V3Cmd>(new V3Simulator(), settings);
        p.Reset(new V3State(Vector2.Zero, 0f));
        return p;
    }

    private const float Tick = 1f / 60f;

    [Fact]
    public void Predict_AssignsIncreasingSeq_AndAdvancesState()
    {
        var p = NewPrediction();
        int s0 = p.Predict(new Vector2(60f, 0f)); // 60 * (1/60) = 1 unit
        int s1 = p.Predict(new Vector2(60f, 0f));
        Assert.Equal(0, s0);
        Assert.Equal(1, s1);
        Assert.Equal(2f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_matching_basis_reports_zero_error_and_stays_continuous()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        p.Predict(new Vector2(60f, 0f)); // predicted X = 2
        p.AdvancePresentation(Tick);     // render catches up to the current tick
        float before = p.RenderedState.Position.X;
        var r = p.Reconcile(authoritativeTick: 1, new FakeState(new Vector2(2f, 0f)), lastAcknowledgedSeq: 1);
        Assert.Equal(0f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(before, p.RenderedState.Position.X, 3);                      // no discontinuity
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3); // already at predicted (zero offset)
    }

    [Fact]
    public void Reconcile_misprediction_sets_a_smoothing_offset_that_decays_to_zero()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // seq 0, predicted X = 10
        p.AdvancePresentation(Tick);      // render catches up: rendered X = 10
        float before = p.RenderedState.Position.X;
        Assert.Equal(10f, before, 3);
        // basis shifted -5 from predicted; seq 0 unacked -> replay puts predicted at 15
        var r = p.Reconcile(1, new FakeState(new Vector2(5f, 0f)), lastAcknowledgedSeq: -1);
        Assert.Equal(5f, r.PositionError, 3);   // full-tick misprediction magnitude (10 -> 15)
        Assert.False(r.HardSnapApplied);
        Assert.Equal(15f, p.PredictedState.Position.X, 3);
        Assert.Equal(before, p.RenderedState.Position.X, 3); // continuity: rendered holds at 10, no pop
        for (int i = 0; i < 300; i++) p.AdvancePresentation(Tick);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3); // eased to predicted (15)
    }

    [Fact]
    public void Reconcile_LargeError_HardSnaps_NoOffset()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // predicted X = 10
        var r = p.Reconcile(1, new FakeState(new Vector2(200f, 0f)), lastAcknowledgedSeq: -1);
        Assert.True(r.HardSnapApplied);
        Assert.Equal(210f, p.PredictedState.Position.X, 3);
        Assert.Equal(210f, p.RenderedState.Position.X, 3); // snapped, no smoothing
    }

    [Fact]
    public void Reconcile_AcknowledgedCommands_ArePruned()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // 0
        p.Predict(new Vector2(60f, 0f)); // 1
        p.Predict(new Vector2(60f, 0f)); // 2
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: 2); // all acked, none replayed
        Assert.Equal(0f, p.PredictedState.Position.X, 3);
        Assert.Equal(3, p.Predict(new Vector2(60f, 0f))); // next seq continues
    }

    [Fact]
    public void MaxPendingCommands_DropsOldest()
    {
        var p = NewPrediction(PredictionSettings.Default with { MaxPendingCommands = 4 });
        for (int i = 0; i < 6; i++) p.Predict(new Vector2(60f, 0f)); // 6 predicted -> X = 6
        // nothing acked; only the last 4 commands remain to replay from origin -> X = 4
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(4f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void Offset_within_the_dead_zone_is_zeroed_on_the_next_presentation_step()
    {
        // The dead-zone now governs the presentation-side cleanup: once the decaying smoothing offset shrinks within
        // it, the render snaps exactly onto the predicted state instead of chasing float jitter forever.
        var settings = PredictionSettings.Default with { CorrectionDeadZone = 0.5f };
        var p = NewPrediction(settings);
        p.Predict(new Vector2(600f, 0f)); // predicted 10
        p.AdvancePresentation(Tick);      // rendered 10
        // correction of 0.4 (< 0.5 dead-zone): seq unacked, basis puts predicted at 10.4
        p.Reconcile(1, new FakeState(new Vector2(0.4f, 0f)), lastAcknowledgedSeq: -1);
        p.AdvancePresentation(Tick);      // |offset| 0.4 < dead-zone 0.5 -> snapped to zero this step
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 6);
    }

    [Fact]
    public void Reset_ClearsPendingAndSeq()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        p.Predict(new Vector2(60f, 0f));
        p.Reset(new FakeState(new Vector2(99f, 0f)));
        Assert.Equal(99f, p.PredictedState.Position.X, 3);
        Assert.Equal(0, p.Predict(new Vector2(60f, 0f))); // seq restarts at 0
        // only the one post-reset command should replay (pre-reset commands were cleared)
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(1f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void RenderedState_eases_between_ticks_so_it_stays_smooth_above_the_tick_rate()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // predicted X steps to 1; render starts at the previous tick (0)
        Assert.Equal(1f, p.PredictedState.Position.X, 3);
        Assert.Equal(0f, p.RenderedState.Position.X, 3);

        // Half a tick of frames -> render is halfway from the previous (0) to the current (1) tick.
        p.AdvancePresentation(Tick * 0.5f);
        Assert.Equal(0.5f, p.RenderedState.Position.X, 2);

        // The rest of the tick -> render reaches the current tick.
        p.AdvancePresentation(Tick * 0.5f);
        Assert.Equal(1f, p.RenderedState.Position.X, 2);

        // No new predict: render holds at the current tick (clamped, no overshoot past it).
        p.AdvancePresentation(Tick);
        Assert.Equal(1f, p.RenderedState.Position.X, 3);
    }

    [Fact]
    public void RenderedState_eases_the_vertical_axis_between_ticks()
    {
        var p = NewV3();
        p.Predict(new V3Cmd(Vector2.Zero, 60f)); // height steps to 1; render starts at the previous tick (0)
        Assert.Equal(1f, p.PredictedState.Height, 3);
        Assert.Equal(0f, p.RenderedState.Height, 3);

        p.AdvancePresentation(Tick * 0.5f);
        Assert.Equal(0.5f, p.RenderedState.Height, 2); // vertical eases like the planar axis

        p.AdvancePresentation(Tick * 0.5f);
        Assert.Equal(1f, p.RenderedState.Height, 2);
    }

    [Fact]
    public void Sub_hardsnap_vertical_correction_eases_over_frames_without_a_pop()
    {
        var p = NewV3();
        for (int i = 0; i < 3; i++) p.Predict(new V3Cmd(Vector2.Zero, 60f)); // climb to height ~3
        p.AdvancePresentation(Tick);                                          // render catches up to ~3
        float before = p.RenderedState.Height;
        Assert.Equal(3f, before, 2);

        // Server says the height is 1 (a 2-unit vertical misprediction), all commands acked.
        var r = p.Reconcile(1, new V3State(Vector2.Zero, 1f), lastAcknowledgedSeq: 2);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(1f, p.PredictedState.Height, 3);                // predicted corrected to the basis
        Assert.Equal(before, p.RenderedState.Height, 3);             // NO instantaneous vertical pop

        p.AdvancePresentation(Tick);
        float oneStep = p.RenderedState.Height;
        Assert.True(oneStep < before - 1e-3f && oneStep > 1f + 1e-3f, // eased PART of the way, not all in one step
            $"vertical should ease, got {oneStep} (from {before} toward 1)");

        for (int i = 0; i < 300; i++) p.AdvancePresentation(Tick);
        Assert.Equal(1f, p.RenderedState.Height, 2);                 // converged to the predicted height
    }

    [Fact]
    public void Mid_tick_reconcile_does_not_jump_the_rendered_position()
    {
        // The remote-jitter fix: a snapshot arriving mid inter-tick must not pop the avatar forward by the
        // un-played remainder of the interpolation. Before the fix, Reconcile collapsed the inter-tick lerp.
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));      // predicted X = 1, previous tick = 0
        p.AdvancePresentation(Tick * 0.5f);   // mid-tick: rendered X = 0.5
        float before = p.RenderedState.Position.X;
        Assert.Equal(0.5f, before, 2);

        // A snapshot lands mid-tick; the basis matches the prediction exactly (seq 0 acked) - no real correction.
        p.Reconcile(1, new FakeState(new Vector2(1f, 0f)), lastAcknowledgedSeq: 0);
        Assert.Equal(before, p.RenderedState.Position.X, 3); // continuous: no forward pop
    }

    [Fact]
    public void Mid_tick_reconcile_does_not_jump_the_rendered_vertical_axis()
    {
        var p = NewV3();
        p.Predict(new V3Cmd(Vector2.Zero, 60f)); // height to 1, previous tick = 0
        p.AdvancePresentation(Tick * 0.5f);      // mid-tick: rendered height = 0.5
        float before = p.RenderedState.Height;
        Assert.Equal(0.5f, before, 2);

        p.Reconcile(1, new V3State(Vector2.Zero, 1f), lastAcknowledgedSeq: 0); // matching basis mid-tick
        Assert.Equal(before, p.RenderedState.Height, 3); // continuous vertical, no pop
    }
}
