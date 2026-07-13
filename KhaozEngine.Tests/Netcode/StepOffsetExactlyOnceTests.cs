using KhaozEngine.Netcode;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

// E5 exactly-once: ClientPrediction.StepCumulativeY is the session-monotonic running sum of discrete-step impulses a
// render mesh smoother DIFFS to ease an isolated step. It MUST be incremented exactly once per real forward tick and
// NEVER re-counted when reconciliation replays the pending command window across that same step tick - otherwise the
// mesh offset would be applied once per reconcile (multiple times) instead of once. These drive Predict + Reconcile
// across a step tick and pin that the accumulator moves by exactly the step delta, regardless of how many reconciles
// (or acks) pass over it. RED if StepDeltaY were consumed off the reconciled state instead of at the Predict boundary.
public class StepOffsetExactlyOnceTests
{
    // A predicted state carrying a discrete-step impulse. The impulse is a per-tick OUTPUT (set by the simulator from the
    // command), so a replay re-produces it deterministically - the exact condition under which a naive consumer would
    // double-count.
    private readonly record struct StepState(Vector2 Pos, float Step) : IPredictedState<StepState>
    {
        Vector2 IPredictedState<StepState>.Position => Pos;
        float IPredictedState<StepState>.StepDeltaY => Step;
        public StepState WithPosition(Vector2 position) => this with { Pos = position };
    }

    // Command = a planar move plus the discrete-step delta this tick commits (0 on a non-step tick).
    private readonly record struct StepCmd(Vector2 Move, float StepDelta);

    private sealed class StepSimulator : ITickSimulator<StepState, StepCmd>
    {
        public StepState Step(in StepState state, in StepCmd command, float dt)
            => new(state.Pos + command.Move * dt, command.StepDelta);
    }

    const float Tick = 1f / 60f;

    static ClientPrediction<StepState, StepCmd> New()
    {
        var p = new ClientPrediction<StepState, StepCmd>(new StepSimulator());
        p.Reset(new StepState(Vector2.Zero, 0f));
        return p;
    }

    // Build the authoritative basis (server-agreed state) by replaying the commands acknowledged so far (seq 0..ackSeq)
    // from the seed with a fresh simulator. This is a MATCHING reconcile: replaying the still-pending commands on top
    // reproduces the predicted state, so there is no hard-snap. Note the basis carries StepDeltaY == 0 (a per-tick output,
    // it does not ride the authoritative snapshot), exactly like the real PlayerMoveState.From path.
    static StepState BasisFor(IReadOnlyList<StepCmd> cmds, int ackSeq)
    {
        var sim = new StepSimulator();
        var s = new StepState(Vector2.Zero, 0f);
        for (int i = 0; i <= ackSeq && i < cmds.Count; i++) s = sim.Step(s, cmds[i], Tick);
        return new StepState(s.Pos, 0f);
    }

    [Fact]
    public void Predict_FoldsStepDelta_OncePerTick()
    {
        var p = New();
        Assert.Equal(0f, p.StepCumulativeY, 5);
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0f));
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0f));
        Assert.Equal(0f, p.StepCumulativeY, 5);                 // no step yet
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0.30f));     // the step tick
        Assert.Equal(0.30f, p.StepCumulativeY, 5);
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0f));
        Assert.Equal(0.30f, p.StepCumulativeY, 5);              // stays put on non-step ticks
        p.Predict(new StepCmd(new Vector2(1f, 0f), -0.20f));    // a later step-down
        Assert.Equal(0.10f, p.StepCumulativeY, 5);
    }

    [Fact]
    public void Reconcile_ReplayingStepTick_NeverDoubleCounts()
    {
        var p = New();
        var cmds = new List<StepCmd>
        {
            new(new Vector2(1f, 0f), 0f),
            new(new Vector2(1f, 0f), 0f),
            new(new Vector2(1f, 0f), 0.30f),   // seq 2: the step commit
            new(new Vector2(1f, 0f), 0f),
            new(new Vector2(1f, 0f), 0f),
        };
        for (int i = 0; i < cmds.Count; i++) p.Predict(cmds[i]);
        Assert.Equal(0.30f, p.StepCumulativeY, 5);

        // Nothing acked yet: reconcile REPLAYS the whole window (seq 0..4) including the step tick, many times. Each
        // replay re-runs Step and re-produces StepDeltaY = 0.30 on seq 2 - a naive "read StepDeltaY off the reconciled
        // state" consumer would add it every reconcile. The Predict-boundary accumulator must not move.
        for (int r = 0; r < 5; r++)
        {
            p.Reconcile(authoritativeTick: r, BasisFor(cmds, ackSeq: -1), lastAcknowledgedSeq: -1);
            Assert.Equal(0.30f, p.StepCumulativeY, 5);
        }

        // Now the step tick (seq 2) is ACKNOWLEDGED and drops out of the pending window: the replay no longer re-produces
        // it, but the accumulator must KEEP the step (it is permanent - the mesh already eased it; it must not vanish).
        p.Reconcile(authoritativeTick: 10, BasisFor(cmds, ackSeq: 2), lastAcknowledgedSeq: 2);
        Assert.Equal(0.30f, p.StepCumulativeY, 5);
        // A few more reconciles past the ack: still exactly one step's worth.
        for (int r = 0; r < 3; r++)
        {
            p.Reconcile(authoritativeTick: 11 + r, BasisFor(cmds, ackSeq: 4), lastAcknowledgedSeq: 4);
            Assert.Equal(0.30f, p.StepCumulativeY, 5);
        }
    }

    [Fact]
    public void InterleavedPredictAndReconcile_AccumulatesEachStepExactlyOnce()
    {
        // Two step commits interleaved with reconciles at every tick (the worst case: a reconcile fires between each
        // Predict, replaying the growing pending window each time). The final accumulator is the sum of the two DISTINCT
        // step deltas, not that sum times the reconcile count.
        var p = New();
        var cmds = new List<StepCmd>();
        void Step(StepCmd c, int ack)
        {
            cmds.Add(c);
            p.Predict(c);
            p.Reconcile(authoritativeTick: cmds.Count, BasisFor(cmds, ack), lastAcknowledgedSeq: ack);
        }
        Step(new StepCmd(new Vector2(1f, 0f), 0f), -1);
        Step(new StepCmd(new Vector2(1f, 0f), 0.30f), -1);   // step-up
        Step(new StepCmd(new Vector2(1f, 0f), 0f), 0);
        Step(new StepCmd(new Vector2(1f, 0f), -0.15f), 1);   // step-down
        Step(new StepCmd(new Vector2(1f, 0f), 0f), 3);       // acks the step-down out of pending
        Step(new StepCmd(new Vector2(1f, 0f), 0f), 4);
        Assert.Equal(0.15f, p.StepCumulativeY, 5);           // 0.30 - 0.15, each counted exactly once
    }

    [Fact]
    public void ResetAndReseed_ZeroTheAccumulator()
    {
        var p = New();
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0.30f));
        Assert.Equal(0.30f, p.StepCumulativeY, 5);
        p.Reset(new StepState(Vector2.Zero, 0f));
        Assert.Equal(0f, p.StepCumulativeY, 5);              // join/respawn re-baseline
        p.Predict(new StepCmd(new Vector2(1f, 0f), 0.10f));
        Assert.Equal(0.10f, p.StepCumulativeY, 5);
        p.Reseed(new StepState(new Vector2(9f, 9f), 0f));
        Assert.Equal(0f, p.StepCumulativeY, 5);              // reconnect re-baseline
    }
}
