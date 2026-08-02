using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Game;

// The RECONCILE-PARITY guard for authoritative facing, in the StairGlideReconcileParityTests shape: a lag-free
// continuous authoritative chain, and the SAME command stream driven through ClientPrediction with a periodic
// reconcile at a realistic ack lag, with the basis rebuilt the only way a client can rebuild it - through
// MovementState.From + PlayerMoveState.From.
//
// MoveState.FacingYaw is CARRIED state: a FINITE MoveTuning.FacingTurnSpeed means this tick's heading is the previous
// tick's heading plus a bounded step, so the replay needs the authoritative heading to know where it is turning FROM.
// ClientPrediction.Reconcile does `TState replayed = authoritativeBasis`, an unconditional overwrite, so a heading
// missing from the replicated seed does not lag: it RESETS to 0 on every correction and the replayed character
// restarts its turn from due -Z several times a second. That is the failure this pins, and it is exactly why the
// heading rides the wire (MovementState.FacingYawQ) where the one-tick LandingImpactSpeed latch deliberately does not.
//
// The bar is the wire quantum, not zero, for the same reason AirMomentumReplicationTests uses it: the authoritative
// head carries a full-precision float and the client's basis arrives through a 16-bit turn fraction, so a re-seeded
// replay can never be bit-identical to the continuous stream. It does not COMPOUND (each reconcile re-seeds from a
// fresh authoritative value and the turn is additive on top), which is what the per-tick bar below actually asserts.
public class FacingReconcileParityTests
{
    const float Dt = 1f / 30f;
    const float Quantum = MovementState.FacingYawQuantum;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    readonly record struct Frame(Vector3 Pos, float Facing);

    static Frame Snap(in PlayerMoveState s) => new(s.Position, s.Move.FacingYaw);

    // A camera sweep FASTER than the turn rate below, so the heading is genuinely mid-turn on every single tick and
    // repeatedly crosses the +pi/-pi seam. A sweep the rate could keep up with would let both streams sit parked on
    // the target, where a dropped carry is invisible.
    static MoveCommand Cmd(int i) =>
        new(new Vector2(1f, 0.5f), run: false, cameraYaw: CharacterMovement.WrapYaw(i * 0.09f), jump: false,
            faceCamera: true);

    static (List<Frame> cont, List<Frame> recon) DriveContinuousAndReconciled(float turnSpeed, int lag, int ticks)
    {
        MoveTuning t = MoveTuning.Default with { FacingTurnSpeed = turnSpeed };
        var sim = new PlayerMoveSimulator(Flat, t);
        var seed = new PlayerMoveState
        {
            Move = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true },
        };

        var contStates = new List<PlayerMoveState> { seed };
        for (int j = 0; j < ticks; j++) contStates.Add(sim.Step(contStates[j], Cmd(j), Dt));

        var cont = new List<Frame>();
        for (int j = 1; j <= ticks; j++) cont.Add(Snap(contStates[j]));

        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(seed);
        var recon = new List<Frame>();
        for (int i = 0; i < ticks; i++)
        {
            pred.Predict(Cmd(i));   // returns seq i
            if (i >= lag)
            {
                // The authoritative state that has folded command (i - lag), rebuilt through the replicated
                // components alone. Pending = seqs (i - lag + 1 .. i), replayed on top.
                int ackSeq = i - lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                PlayerMoveState basis = PlayerMoveState.From(authFull.Position, MovementState.From(authFull));
                pred.Reconcile(i, basis, ackSeq);
            }
            recon.Add(Snap(pred.PredictedState));
        }
        return (cont, recon);
    }

    [Theory]
    [InlineData(1.5f)]                        // a finite rate: the heading is mid-turn on every tick
    [InlineData(float.PositiveInfinity)]      // the default snap: only the quantum separates the two
    public void AMidTurnHeadingSurvivesReconciliation(float turnSpeed)
    {
        const int Lag = 4;    // ~130 ms RTT at 30 Hz, the same realistic short window the stair-glide parity test uses
        const int Ticks = 220;   // long enough for the SLOWEST case here (1.5 rad/s) to turn several radians and wrap
        var (cont, recon) = DriveContinuousAndReconciled(turnSpeed, Lag, Ticks);

        // Harness validity. If the heading is not actually turning, and turning across the seam, the parity assertion
        // below is green for the wrong reason and pins nothing.
        float travelled = 0f;
        bool crossedTheSeam = false;
        for (int i = 1; i < Ticks; i++)
        {
            float step = CharacterMovement.WrapYaw(cont[i].Facing - cont[i - 1].Facing);
            travelled += MathF.Abs(step);
            if (MathF.Abs(cont[i].Facing - cont[i - 1].Facing) > MathF.PI) crossedTheSeam = true;
        }
        Assert.True(travelled > 8f, $"the fixture only turned {travelled} rad, too little to prove anything");
        Assert.True(crossedTheSeam, "the fixture never crossed the +pi/-pi seam, where a wrap bug would show");

        float maxFacingGap = 0f, maxPosErr = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            maxFacingGap = MathF.Max(maxFacingGap, MathF.Abs(CharacterMovement.WrapYaw(recon[i].Facing - cont[i].Facing)));
            maxPosErr = MathF.Max(maxPosErr, Vector3.Distance(recon[i].Pos, cont[i].Pos));
        }
        string metrics = $"turnSpeed={turnSpeed} lag={Lag}: maxFacingGap={maxFacingGap} rad (quantum {Quantum}), " +
                         $"maxPosErr={maxPosErr} m over {travelled:F1} rad of turning";

        // Position is untouched by facing, and the sim is deterministic with matching inputs, so the replayed
        // positions are EXACT. This is also the harness's own check that the reconcile is not merely doing nothing.
        Assert.Equal(0f, maxPosErr);
        Assert.True(maxFacingGap <= 2f * Quantum, metrics + " - the replayed heading is more than two quanta off");
    }

    [Fact]
    public void WithoutTheCarry_TheReplayWouldRestartEveryTurn()
    {
        // The counter-fixture: the same replay with the heading DELIBERATELY stripped out of the basis, which is what
        // a MovementState missing FacingYawQ would produce. It has to fail loudly, or the assertion above is not
        // measuring anything. The gap is radians, not quanta - a visibly different direction several times a second.
        const int Lag = 4;
        const int Ticks = 120;
        MoveTuning t = MoveTuning.Default with { FacingTurnSpeed = 1.5f };
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
        float worst = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            pred.Predict(Cmd(i));
            if (i >= Lag)
            {
                int ackSeq = i - Lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                MovementState wire = MovementState.From(authFull);
                wire.FacingYawQ = 0;   // the field absent from the codec, simulated
                pred.Reconcile(i, PlayerMoveState.From(authFull.Position, wire), ackSeq);
            }
            worst = MathF.Max(worst,
                MathF.Abs(CharacterMovement.WrapYaw(pred.PredictedState.Move.FacingYaw - contStates[i + 1].Move.FacingYaw)));
        }

        Assert.True(worst > 1f, $"stripping the heading from the basis only cost {worst} rad, so the guard is weak");
    }
}
