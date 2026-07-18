using KhaozEngine.Netcode;
using System;
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

    // A planar state that also carries the authoritative teleport epoch, to exercise the epoch-driven hard cut
    // generically (no NetWorld dependency). The command is a velocity like FakeState. The epoch is not part of the
    // prediction replay - the simulator ignores it - it only rides the authoritative basis into Reconcile.
    private readonly record struct EpochState(Vector2 Position, uint Epoch) : IPredictedState<EpochState>
    {
        uint IPredictedState<EpochState>.TeleportEpoch => Epoch;
        public EpochState WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class EpochSimulator : ITickSimulator<EpochState, Vector2>
    {
        public EpochState Step(in EpochState state, in Vector2 command, float dt)
            => state.WithPosition(state.Position + command * dt);
    }

    private static ClientPrediction<EpochState, Vector2> NewEpoch(uint seedEpoch)
    {
        var p = new ClientPrediction<EpochState, Vector2>(new EpochSimulator());
        p.Reset(new EpochState(Vector2.Zero, seedEpoch));
        return p;
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
    public void Reseed_reseeds_state_but_keeps_the_seq_monotonic_and_keeps_pending()
    {
        // The reconnect path. After a mid-session reconnect the predicted state must jump to the authoritative
        // basis, but the sequence counter must NOT rewind the way Reset rewinds it: the fresh server has already
        // advanced its ack from the commands sent in the join gap, so the next command has to stay ahead of that
        // ack (continue at the high seq), not collide at 0 and be rejected as stale.
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // seq 0, X = 1
        p.Predict(new Vector2(60f, 0f)); // seq 1, X = 2
        p.Predict(new Vector2(60f, 0f)); // seq 2, X = 3 ; nextSeq now 3

        p.Reseed(new FakeState(new Vector2(99f, 0f)));
        Assert.Equal(99f, p.PredictedState.Position.X, 3);   // state re-seeded to the basis
        Assert.Equal(3, p.Predict(new Vector2(0f, 0f)));     // seq continues at 3 (Reset would restart it at 0)

        // Pending was kept (not cleared): a reconcile with nothing acked replays the retained commands on top of
        // the basis. Basis 0, replay seqs 0..3 (1 + 1 + 1 + 0 units) -> X = 3 (Reset-then-replay would give 0).
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(3f, p.PredictedState.Position.X, 3);
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
    public void Sustained_stream_of_small_vertical_corrections_eases_with_inertia_not_a_first_order_chase()
    {
        // The surface-swim / buoyancy profile. The buoyancy spring emits a CONTINUOUS stream of small vertical
        // reconciliation corrections (unlike the one-off jump/fall the other vertical tests cover). A plain
        // first-order (exponential) vertical decay chases each correction immediately - a fast, jerky camera bob for
        // any consumer that follows RenderedState's vertical. Mirroring the planar axis (10.7.0), the vertical offset
        // now decays with the critically-damped, velocity-carrying filter, and its smoothing velocity resets on each
        // re-anchor, so the first presentation frame after EVERY correction is the from-rest (inertial) response:
        // it barely moves, well under the first-order per-frame fraction. A first-order decay would move the full
        // CorrectionRate*dt fraction on that first frame, so this asserts the inertia is present throughout the stream.
        var settings = PredictionSettings.Default;
        var p = NewV3(settings);
        float firstOrderFrac = settings.CorrectionRate * Tick; // fraction a first-order decay moves toward target in one frame
        const float swing = 0.1f;                              // 10 cm buoyancy jitter, comfortably above the 0.03 dead-zone
        float target = 0f;

        for (int i = 0; i < 30; i++)
        {
            target = (i % 2 == 0) ? swing : -swing; // predicted rebases to the alternating basis (spring overshoot each way)
            float renderBefore = p.RenderedState.Height;
            p.Reconcile(i, new V3State(Vector2.Zero, target), lastAcknowledgedSeq: -1); // no pending -> predicted := basis
            Assert.Equal(target, p.PredictedState.Height, 5);       // predicted followed the authoritative basis
            Assert.Equal(renderBefore, p.RenderedState.Height, 5);  // continuity: no pop at the correction

            float gap = MathF.Abs(target - renderBefore);
            p.AdvancePresentation(Tick);                            // one presentation frame after the correction
            float firstStep = MathF.Abs(p.RenderedState.Height - renderBefore);
            Assert.True(firstStep < 0.5f * firstOrderFrac * gap,
                $"correction {i}: render lurched {firstStep} in one frame (first-order would move ~{firstOrderFrac * gap}); " +
                "the critically-damped vertical offset should ease with inertia, not chase");

            for (int f = 0; f < 3; f++) p.AdvancePresentation(Tick); // let the decay progress before the next correction
        }

        // Sustained resolution: hold the basis steady - the offset must still fully converge to the predicted height
        // (inertia slows the start but a sustained correction resolves, unlike a hard "never move back" clamp).
        p.Reconcile(999, new V3State(Vector2.Zero, target), lastAcknowledgedSeq: -1);
        for (int i = 0; i < 600; i++) p.AdvancePresentation(Tick);
        Assert.Equal(target, p.RenderedState.Height, 3);
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
    public void Matching_reconcile_does_not_alter_the_rendered_trajectory()
    {
        // C1 continuity (the local 30 Hz camera-sawtooth fix): a matching mid-tick reconcile - the loopback case,
        // fired every tick - must not perturb the rendered path at all. Two identical predictions: reconcile ONE
        // mid-tick against the exact predicted basis; its rendered position must then track the un-reconciled control
        // frame-for-frame. Before the fix, Reconcile collapsed the inter-tick lerp and the reconciled copy crawled
        // forward on the smoothing-offset decay (a per-tick velocity dip) instead of the steady inter-tick velocity.
        var reconciled = NewPrediction();
        var control = NewPrediction();
        reconciled.Predict(new Vector2(60f, 0f));   // both step to X = 1 over the tick
        control.Predict(new Vector2(60f, 0f));
        reconciled.AdvancePresentation(Tick * 0.5f); // both mid-tick at rendered X = 0.5
        control.AdvancePresentation(Tick * 0.5f);

        reconciled.Reconcile(1, new FakeState(new Vector2(1f, 0f)), lastAcknowledgedSeq: 0); // matching, seq acked

        for (int i = 0; i < 10; i++)
        {
            reconciled.AdvancePresentation(Tick * 0.1f);
            control.AdvancePresentation(Tick * 0.1f);
            Assert.Equal(control.RenderedState.Position.X, reconciled.RenderedState.Position.X, 5);
        }
    }

    [Fact]
    public void Backward_rebase_keeps_the_inter_tick_velocity_and_does_not_drag_the_render_back()
    {
        // The decel-to-stop shake fix. When the local player stops, its prediction halts instantly, but the authority
        // is an input-RTT behind, so the basis the client reconciles against dips BACKWARD for a tick or two before it
        // catches up. Here: predict a stop at X = 1, then reconcile mid-tick against a basis a full tick behind (X = 0,
        // all commands acked -> predicted rebases back to 0). The reconcile must stay continuous (no pop), and - the
        // fix - completing the tick must NOT drag the render back toward the rebased target: the inter-tick segment is
        // translated by the rebase delta so its (zero, for a stopped player) velocity is preserved, and the whole jump
        // is absorbed into the critically-damped offset. Pre-fix, previousPredictedPosition stayed at 1 while the
        // target moved to 0, so the inter-tick lerp hauled the render down to ~0.5 across the rest of the tick.
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // predicted X = 1
        p.AdvancePresentation(Tick);     // render catches up to 1
        p.Predict(Vector2.Zero);         // stop: predicted holds at 1
        p.AdvancePresentation(Tick * 0.5f); // mid-tick; a stopped player does not move, so render is still 1
        float before = p.RenderedState.Position.X;
        Assert.Equal(1f, before, 3);

        // Authority a full tick behind, everything acked -> predicted rebases backward to 0.
        var r = p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: 0);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(0f, p.PredictedState.Position.X, 3);       // predicted followed authority back
        Assert.Equal(before, p.RenderedState.Position.X, 3);    // but the RENDER did not pop

        // Finish the tick with no further snapshot. The render must hold near 1 (only the offset eases it), not get
        // dragged toward the rebased-back target by the inter-tick lerp.
        p.AdvancePresentation(Tick * 0.5f);
        Assert.True(p.RenderedState.Position.X > 0.9f,
            $"backward rebase dragged the render back to {p.RenderedState.Position.X:F3} (C1 rebase should hold it near 1)");
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

    [Fact]
    public void PredictedHorizontalSpeed_equals_per_tick_distance_over_tick_seconds()
    {
        var p = NewPrediction();
        Assert.Equal(0f, p.PredictedHorizontalSpeed, 3); // no command yet

        // A 5-12 vector has length 13; the tick advances by command * Tick so the per-tick distance is 13 * Tick,
        // and the reported speed is that distance / Tick == 13. (Length, not just the X axis - planar magnitude.)
        p.Predict(new Vector2(5f, 12f));
        Assert.Equal(13f, p.PredictedHorizontalSpeed, 3);

        // Steady input -> steady speed every tick.
        p.Predict(new Vector2(5f, 12f));
        Assert.Equal(13f, p.PredictedHorizontalSpeed, 3);
    }

    [Fact]
    public void PredictedHorizontalSpeed_is_zero_under_zero_input()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));   // moving
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3);
        p.Predict(Vector2.Zero);           // standing still
        Assert.Equal(0f, p.PredictedHorizontalSpeed, 3);
    }

    [Fact]
    public void PredictedHorizontalSpeed_ignores_the_reconcile_rebase_and_tracks_the_next_live_tick()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // steady run, speed 60
        p.Predict(new Vector2(60f, 0f));
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3);

        // A reconcile that rebases the predicted position a long way (a big snap) must NOT register as a huge
        // instantaneous speed: speed is computed only in Predict, not over the rebase. It holds the last live value.
        p.Reconcile(1, new FakeState(new Vector2(500f, 0f)), lastAcknowledgedSeq: -1);
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3); // unchanged by the rebase, not 500-ish

        // The next real commanded tick reports the steady commanded speed (off the rebased basis, not the snap).
        p.Predict(new Vector2(60f, 0f));
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3);
    }

    [Fact]
    public void Reconcile_epoch_advance_forces_a_hard_snap_below_the_hardsnap_distance()
    {
        var p = NewEpoch(seedEpoch: 0);
        p.Reconcile(0, new EpochState(Vector2.Zero, 0), lastAcknowledgedSeq: -1); // settle the seed (consume the join signal)

        p.Predict(new Vector2(60f, 0f)); // predicted X = 1
        p.AdvancePresentation(Tick);     // rendered catches up to 1
        // A 4-unit correction, well under HardSnapDistance (100), but the authoritative epoch ADVANCED 0 -> 1: cut.
        var r = p.Reconcile(1, new EpochState(new Vector2(5f, 0f), 1), lastAcknowledgedSeq: 0);
        Assert.True(r.HardSnapApplied, "an epoch advance must force a hard snap regardless of distance");
        Assert.True(r.Teleported);
        Assert.Equal(5f, p.PredictedState.Position.X, 3);
        Assert.Equal(5f, p.RenderedState.Position.X, 3);   // snapped: no residual smoothing offset
    }

    [Fact]
    public void Reconcile_without_an_epoch_advance_glides_a_sub_hardsnap_correction()
    {
        var p = NewEpoch(seedEpoch: 7);
        p.Reconcile(0, new EpochState(Vector2.Zero, 7), lastAcknowledgedSeq: -1); // settle the seed; lastEpoch = 7

        p.Predict(new Vector2(60f, 0f)); // predicted X = 1
        p.AdvancePresentation(Tick);     // rendered = 1
        float before = p.RenderedState.Position.X;
        // Same correction, but the epoch is UNCHANGED (7): the sub-hardsnap error glides, no cut, no signal.
        var r = p.Reconcile(1, new EpochState(new Vector2(5f, 0f), 7), lastAcknowledgedSeq: 0);
        Assert.False(r.HardSnapApplied);
        Assert.False(r.Teleported);
        Assert.Equal(before, p.RenderedState.Position.X, 3);   // continuity: no pop, the correction eases in
    }

    [Fact]
    public void First_reconcile_after_reset_reports_teleported_then_steady_does_not()
    {
        // The uniform join signal: the first reconcile after a Reset seed reports a teleport (so the consumer snaps
        // the camera on login), and a subsequent steady reconcile at the same epoch does not re-fire.
        var p = NewEpoch(seedEpoch: 3);
        var r = p.Reconcile(0, new EpochState(Vector2.Zero, 3), lastAcknowledgedSeq: -1);
        Assert.True(r.Teleported);
        var r2 = p.Reconcile(1, new EpochState(Vector2.Zero, 3), lastAcknowledgedSeq: -1);
        Assert.False(r2.Teleported);
    }

    [Fact]
    public void First_reconcile_after_reseed_reports_teleported_without_double_firing()
    {
        // The reconnect signal: Reseed captures the epoch it seeds on, so the first post-reseed reconcile fires the
        // teleport once (from the seed), not twice (it must not also count the seed epoch as an advance).
        var p = NewEpoch(seedEpoch: 1);
        p.Reconcile(0, new EpochState(Vector2.Zero, 1), lastAcknowledgedSeq: -1); // consume the join signal
        p.Reseed(new EpochState(new Vector2(50f, 0f), 5));
        var r = p.Reconcile(1, new EpochState(new Vector2(50f, 0f), 5), lastAcknowledgedSeq: -1);
        Assert.True(r.Teleported);
        var r2 = p.Reconcile(2, new EpochState(new Vector2(50f, 0f), 5), lastAcknowledgedSeq: -1);
        Assert.False(r2.Teleported);   // steady after the reseed: no re-fire
    }

    [Fact]
    public void PredictedHorizontalSpeed_is_zeroed_by_Reset_and_Reseed()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3);
        p.Reset(new FakeState(Vector2.Zero));
        Assert.Equal(0f, p.PredictedHorizontalSpeed, 3);

        p.Predict(new Vector2(60f, 0f));
        Assert.Equal(60f, p.PredictedHorizontalSpeed, 3);
        p.Reseed(new FakeState(Vector2.Zero));
        Assert.Equal(0f, p.PredictedHorizontalSpeed, 3);
    }
}
