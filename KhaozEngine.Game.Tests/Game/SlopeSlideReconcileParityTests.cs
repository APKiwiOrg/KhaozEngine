using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Game;

// THE RECONCILE-PARITY GUARD FOR THE SLOPE SLIDE (#442, 17.27.0), in the StairGlideReconcileParityTests shape.
//
// A slide is CARRIED velocity: this tick's fall-line speed is the previous tick's plus a tangential-gravity step,
// held in MoveState.HorizontalVelocity and MoveState.VerticalVelocity. ClientPrediction.Reconcile rebuilds the
// whole predicted state from the wire-decoded basis (`TState replayed = authoritativeBasis`, an unconditional
// overwrite) and replays the pending command window on top of it, so a slide only survives a correction if both
// carried components reach the basis through PlayerMoveState.From. They do, and - the load-bearing part - they do
// it UNCONDITIONALLY, with no reference to MoveTuning.AirMomentum: HorizontalVelocityXQ/ZQ have ridden the wire
// since generation 7, MovementState.From always quantizes them and PlayerMoveState.From always decodes them, on
// both server heads. This suite is what pins that, because the field's ONLY consumer before 17.27.0 was the
// opt-in momentum path, so "the carry is dead with the knob off" was a live and wrong assumption to hold.
//
// Both cases run: AirMomentum OFF (the shipped default, and the one that would silently desync if the carry were
// gated on the knob) and ON. The bar is the wire quantum the carry is encoded at, not exactness: the basis is
// quantized to 1/256 m/s per axis, so a replayed slide sits within a quantum's worth of drift of the continuous
// head and no further.
public class SlopeSlideReconcileParityTests
{
    const float Dt = 1f / 30f;
    const float EdgeX = 5f;
    const float SteepGrade = 5f;                                   // 78.7 deg, well past the 45 deg gate
    const float VelocityQuantum = MovementState.HorizontalVelocityQuantum;   // 1/256 m/s per axis

    static MoveTuning Tuning(bool airMomentum) => MoveTuning.Default with { AirMomentum = airMomentum };

    // A mid-slide chain driven twice: once continuously (the lag-free ground truth) and once through
    // ClientPrediction with a periodic reconcile at ack lag `lag`, whose basis is rebuilt through the real wire
    // codec (MovementState.From then PlayerMoveState.From), exactly as WorldClient does. The face is flat at 0
    // west of EdgeX and rises east of it at `grade`, with the normal that describes exactly that surface;
    // `startOffset` seats the character that far up it, far enough that the whole window stays on the face.
    static (List<PlayerMoveState> cont, List<PlayerMoveState> recon) DriveContinuousAndReconciled(
        bool airMomentum, int lag, int ticks, float grade, float startOffset)
    {
        MoveTuning tuning = Tuning(airMomentum);
        float Ground(float x, float z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Vector3 Normal(float x, float z) => x < EdgeX ? Vector3.UnitY : faceNormal;
        var sim = new PlayerMoveSimulator(Ground, tuning, Normal);
        // Steering ACROSS the fall line (world -Z under yaw 0 is the command's forward), so the slide's own
        // fall-line velocity, its contour carry, and the input steer are all live on every tick of the window.
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);

        float StartX = EdgeX + startOffset;
        var seed = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(StartX, Ground(StartX, 0f) + tuning.CapsuleHalfHeight, 0f),
                Grounded = false,
                TimeSinceGrounded = 1f,
            },
        };

        var contStates = new List<PlayerMoveState> { seed };
        for (int j = 0; j < ticks; j++) contStates.Add(sim.Step(contStates[j], cmd, Dt));
        var cont = new List<PlayerMoveState>();
        for (int j = 1; j <= ticks; j++) cont.Add(contStates[j]);

        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(seed);
        var recon = new List<PlayerMoveState>();
        for (int t = 0; t < ticks; t++)
        {
            pred.Predict(cmd);   // returns seq t
            if (t >= lag)
            {
                int ackSeq = t - lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                MovementState wire = MovementState.From(authFull);            // the real quantizers
                PlayerMoveState basis = PlayerMoveState.From(authFull.Position, wire);
                pred.Reconcile(t, basis, ackSeq);
            }
            recon.Add(pred.PredictedState);
        }
        return (cont, recon);
    }

    [Theory]
    // Two steepnesses, and the pair is the point. On the 78.7 deg face the carry is nearly all VERTICAL (the
    // horizontal is only 0.196 of the fall-line speed), so the two quantized horizontal axes barely matter. At
    // 48 deg they carry most of it - the horizontal is 0.67 of the fall-line speed against a vertical 0.74 - so
    // the wire's 1/256 m/s per axis is doing real work on the replayed basis, which is exactly the case a suite
    // that only ever tested a sea cliff would never have exercised.
    [InlineData(false, 5f, 6f)]      // 78.7 deg. Momentum OFF is the shipped default and the desync candidate:
    [InlineData(true, 5f, 6f)]       // the carry has NO other consumer with the knob off.
    [InlineData(false, 1.1106f, 30f)]   // 48.0 deg, the mid-steepness case, both knob settings again
    [InlineData(true, 1.1106f, 30f)]
    public void A_mid_slide_reconcile_tracks_the_continuous_head(bool airMomentum, float grade, float startOffset)
    {
        const int Lag = 4;      // ~130 ms RTT at 30 Hz
        const int Ticks = 60;
        var (cont, recon) = DriveContinuousAndReconciled(airMomentum, Lag, Ticks, grade, startOffset);
        string tag = $"{(airMomentum ? "momentum on" : "momentum off")}, grade {grade:F4}";

        // Harness validity: the fixture must actually be sliding for most of the window, or this proves nothing.
        int sliding = 0;
        for (int i = 0; i < Ticks; i++) if (!cont[i].Grounded && cont[i].Position.X > EdgeX) sliding++;
        Assert.True(sliding > 30, $"{tag}: degenerate fixture, only {sliding} sliding ticks");
        Assert.True(cont[Ticks - 1].Move.HorizontalVelocity.Length() > 5f,
            $"{tag}: the slide never accumulated speed, |v|={cont[Ticks - 1].Move.HorizontalVelocity.Length():F3}");

        // The bars. Position: the replayed window is driven from a basis whose carried velocity is quantized to
        // 1/256 m/s per axis, and the pending window is `lag` ticks long, so the reachable position gap is about
        // lag * quantum * dt per axis. 1 mm is the bar, and it is a TIGHT one rather than the two orders of
        // headroom an earlier draft of this comment claimed: the measured drift is 0.05 mm on the sea cliff and
        // 0.17 mm at 48 degrees, so a factor of 6 to 20, on a slide running at tens of metres per second.
        float maxPos = 0f, maxVel = 0f, maxVVel = 0f;
        int groundedMismatch = 0;
        for (int i = Lag; i < Ticks; i++)
        {
            maxPos = MathF.Max(maxPos, (recon[i].Position - cont[i].Position).Length());
            maxVel = MathF.Max(maxVel, (recon[i].Move.HorizontalVelocity - cont[i].Move.HorizontalVelocity).Length());
            maxVVel = MathF.Max(maxVVel, MathF.Abs(recon[i].VerticalVelocity - cont[i].VerticalVelocity));
            if (recon[i].Grounded != cont[i].Grounded) groundedMismatch++;
        }

        string metrics = $"{tag}: maxPos={maxPos * 1000f:F3} mm, maxCarry={maxVel * 1000f:F3} mm/s " +
                         $"(quantum {VelocityQuantum * 1000f:F2}), maxVVel={maxVVel * 1000f:F3} mm/s, " +
                         $"groundedMismatch={groundedMismatch}";
        Assert.True(maxPos < 1e-3f, metrics + " - the replayed slide diverged in position");
        // Two quanta covers the per-axis rounding of a two-axis vector difference.
        Assert.True(maxVel <= 2f * VelocityQuantum, metrics + " - the replayed carry diverged beyond the wire quantum");
        // The vertical is replicated RAW, so its only error source is the quantized horizontal feeding the
        // fall-line resolve: one quantum, scaled by the surface's ny/h, which is at most about 1 at these
        // steepnesses. So one quantum is the principled bar. Measured 0.37 mm/s on the sea cliff and 0.96 mm/s at
        // 48 degrees, where the mid-steepness carry is doing the most work - which is also why the flat 1 mm/s
        // this used to assert was about to become a coin toss rather than a bar.
        Assert.True(maxVVel <= VelocityQuantum, metrics + " - the replayed fall-line vertical diverged");
        Assert.Equal(0, groundedMismatch);
    }

    [Fact]
    public void The_carried_slide_velocity_survives_the_wire_with_momentum_off()
    {
        // The narrow, explicit statement of the risk above, with no prediction machinery in the way: a mid-slide
        // state encoded and decoded through the real wire components comes back carrying its fall-line velocity,
        // and one step from the decoded copy lands where one step from the original does. If the carry were ever
        // gated on AirMomentum anywhere along MovementState.From -> PlayerMoveState.From, this is where it shows.
        MoveTuning tuning = Tuning(airMomentum: false);
        float Ground(float x, float z) => x < EdgeX ? 0f : (x - EdgeX) * SteepGrade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
        Vector3 Normal(float x, float z) => x < EdgeX ? Vector3.UnitY : faceNormal;
        var sim = new PlayerMoveSimulator(Ground, tuning, Normal);
        var cmd = MoveCommand.Idle;

        const float StartX = EdgeX + 6f;
        var s = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(StartX, Ground(StartX, 0f) + tuning.CapsuleHalfHeight, 0f),
                Grounded = false,
                TimeSinceGrounded = 1f,
            },
        };
        for (int i = 0; i < 45; i++) s = sim.Step(s, cmd, Dt);
        Assert.True(s.Move.HorizontalVelocity.Length() > 5f,
            $"the fixture never built a slide to carry, |v|={s.Move.HorizontalVelocity.Length():F3}");

        MovementState wire = MovementState.From(s);
        PlayerMoveState decoded = PlayerMoveState.From(s.Position, wire);

        Assert.Equal(s.Move.HorizontalVelocity.X, decoded.Move.HorizontalVelocity.X, 2);
        Assert.Equal(s.Move.HorizontalVelocity.Y, decoded.Move.HorizontalVelocity.Y, 2);
        Assert.Equal(s.VerticalVelocity, decoded.VerticalVelocity);   // replicated raw, not quantized

        PlayerMoveState nextDirect = sim.Step(s, cmd, Dt);
        PlayerMoveState nextDecoded = sim.Step(decoded, cmd, Dt);
        Assert.True((nextDirect.Position - nextDecoded.Position).Length() < 1e-4f,
            $"one step from the decoded basis landed {(nextDirect.Position - nextDecoded.Position).Length() * 1000f:F4} mm away");
    }
}
