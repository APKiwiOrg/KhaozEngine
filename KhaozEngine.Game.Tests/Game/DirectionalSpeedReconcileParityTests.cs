using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Game;

// The RECONCILE-PARITY guard for directional speed scaling (#479), in the FacingReconcileParityTests /
// StairGlideReconcileParityTests shape: a lag-free continuous authoritative chain, and the SAME command stream driven
// through ClientPrediction with a periodic reconcile at a realistic ack lag, with the basis rebuilt the only way a
// client can rebuild it - through MovementState.From + PlayerMoveState.From.
//
// The bar here is EXACT, unlike the facing one. The scale is a pure function of this tick's command and the tuning,
// with no carried state of its own: it reaches the sim as a multiplier on the resolved speed fraction and leaves
// nothing behind for the next tick to read. So a replayed tick sees the same command and the same tuning and must
// resolve the same speed, to the last bit, however many times it is replayed and wherever in the stream the replay
// starts. That is the whole parity argument, and being able to assert zero rather than a tolerance is what proves it:
// a scale that had leaked into carried state (into HorizontalVelocity, say, or into a per-entity SpeedScale) would
// show up here as drift that grows with the reconcile count.
public class DirectionalSpeedReconcileParityTests
{
    const float Dt = 1f / 30f;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static MoveTuning Tuning => MoveTuning.Default with
    {
        StrafeSpeedScale = 0.4f,
        BackpedalSpeedScale = 0.2f,
        BackpedalAllowsRun = false,
    };

    // A command stream that CROSSES sectors while the camera sweeps, so the replay has to re-derive the sector rather
    // than ride one answer for the whole run: the axis walks the unit circle, passing through both boundary rays, and
    // the run bit toggles so the reverse sector's run refusal is exercised mid-stream too.
    static MoveCommand Cmd(int i)
    {
        float a = i * 0.11f;
        return new MoveCommand(new Vector2(MathF.Sin(a), MathF.Cos(a)), run: (i & 1) == 0,
            cameraYaw: CharacterMovement.WrapYaw(i * 0.03f), jump: false, faceCamera: true);
    }

    [Fact]
    public void AMidBackpedalStateSurvivesReconciliationExactly()
    {
        const int Lag = 4;      // ~130 ms RTT at 30 Hz, the same window the sibling parity tests use
        const int Ticks = 240;

        MoveTuning t = Tuning;
        var sim = new PlayerMoveSimulator(Flat, t);
        var seed = new PlayerMoveState
        {
            Move = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true },
        };

        var contStates = new List<PlayerMoveState> { seed };
        for (int j = 0; j < Ticks; j++) contStates.Add(sim.Step(contStates[j], Cmd(j), Dt));

        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(seed);

        float maxPosErr = 0f;
        int reconciles = 0;
        for (int i = 0; i < Ticks; i++)
        {
            pred.Predict(Cmd(i));
            if (i >= Lag)
            {
                int ackSeq = i - Lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                pred.Reconcile(i, PlayerMoveState.From(authFull.Position, MovementState.From(authFull)), ackSeq);
                reconciles++;
            }
            maxPosErr = MathF.Max(maxPosErr, Vector3.Distance(pred.PredictedState.Position, contStates[i + 1].Position));
        }

        // Harness validity: the fixture has to actually visit all three sectors at different speeds, or an exact
        // parity result proves only that the character stood still.
        var seen = new HashSet<MoveSector>();
        float slowest = float.MaxValue, fastest = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            seen.Add(CharacterMovement.Sector(Cmd(i)));
            float speed = contStates[i + 1].Move.CommandedSpeed;
            if (speed < slowest) slowest = speed;
            if (speed > fastest) fastest = speed;
        }
        Assert.Equal(3, seen.Count);
        Assert.True(fastest > slowest * 4f, $"the fixture never changed speed much: {slowest} to {fastest} m/s");
        Assert.True(reconciles > 200, $"only {reconciles} reconciles, too few to show accumulating drift");

        Assert.Equal(0f, maxPosErr);
    }
}
