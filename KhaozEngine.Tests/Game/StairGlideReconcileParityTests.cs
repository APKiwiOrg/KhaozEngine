using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game;

// The RECONCILE-PARITY guard for the signal-driven stair glide. The ascent climb signal is an EWMA
// (MoveState.ClimbRateEwma) that is SIM-LOCAL and NOT replicated: only the derived ClimbRate rides the wire
// (MovementState.ClimbRateQ). So when ClientPrediction.Reconcile rebuilds the local basis via PlayerMoveState.From,
// the EWMA restarts from whatever From seeds it with, and the pending-command window (a few ticks) is SHORTER than
// the EWMA time constant (tau = 1/5 s = 6 ticks). If From leaves the EWMA at 0, every replay restarts the average
// from scratch and the local player's exported ClimbRate reads BELOW the achieved rise for the whole climb -> the
// render feed-forward/damp equilibrium (signal - achieved)/SlopeGlideRate sits BELOW the true feet (a sink of tens
// of mm at walk to ~100 mm at run on short RTT windows), plus a per-reconcile ripple.
//
// This drives the REAL PlayerMoveSimulator (Bepu + CharacterMovement) both as a lag-free continuous chain (the
// ground truth) AND through ClientPrediction with periodic reconciles at a realistic ack lag, and asserts the local
// replayed ClimbRate tracks the continuous stream within one wire quantum, and the render sink stays inside the same
// bars StairGlideEquilibriumTests pins. RED on current code (the reconcile drops the EWMA); GREEN once
// PlayerMoveState.From seeds the EWMA from the wire's decoded rate. GPU-free (a one-bone parked animator).
public class StairGlideReconcileParityTests
{
    const float Riser = 0.30f, Tread = 0.40f;   // grade 0.75, the consumer TestStaircase scale
    const int Risers = 33;
    const float Walk = 3f, Run = 6f;            // Ruinborne consumer tuning
    const float Dt = 1f / 30f;
    const float Quantum = MovementState.ClimbRateQuantum;   // 0.05 m/s wire resolution

    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = Walk, RunSpeed = Run, CapsuleRadius = 0.4f };

    static void AddStairs(IPhysicsWorld world)
    {
        float backZ = -Tread * Risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < Risers; i++)
        {
            float treadTop = Riser * (i + 1);
            float centerZ = 0.5f * (-Tread * i + backZ);
            float depth = -Tread * i - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    readonly struct Frame
    {
        public Frame(Vector3 p, bool g, float vv, float cr) { Pos = p; Grounded = g; VVel = vv; ClimbRate = cr; }
        public Vector3 Pos { get; }
        public bool Grounded { get; }
        public float VVel { get; }
        public float ClimbRate { get; }
    }

    // A continuous (lag-free) sim chain UP the staircase and the SAME chain driven through ClientPrediction with a
    // periodic reconcile at ack lag `lag` (the client is `lag` ticks ahead of the server's ack; the basis is rebuilt
    // through the wire codec via PlayerMoveState.From, exactly as WorldClient does). Both share the one static Bepu
    // world (read-only queries), so a matching-physics replay reproduces the continuous positions exactly and the
    // ONLY divergence between the two ClimbRate streams is the reconcile's EWMA restart.
    static (List<Frame> cont, List<Frame> recon) DriveContinuousAndReconciled(bool run, int lag)
    {
        MoveTuning tuning = Tuning();
        float halfH = tuning.CapsuleHalfHeight, speed = run ? Run : Walk;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(Dt);
        Func<float, float, float> ground = (x, z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var sim = new PlayerMoveSimulator(ground, tuning, normal, physics: world);
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        int ticks = (int)(1.6f * (Tread * Risers + 3f) / (0.5f * speed * Dt));

        var seed = new PlayerMoveState { Move = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true } };

        // Continuous chain: cont[j] carries the full state (including ClimbRateEwma) after j sim steps from the seed.
        var contStates = new List<PlayerMoveState> { seed };
        for (int j = 0; j < ticks; j++)
            contStates.Add(sim.Step(contStates[j], cmd, Dt));

        var cont = new List<Frame>();
        for (int j = 1; j <= ticks; j++)
            cont.Add(new Frame(contStates[j].Position, contStates[j].Grounded, contStates[j].VerticalVelocity, contStates[j].Move.ClimbRate));

        // Reconciled chain: predict each tick, then (once the pending window is full) rebase to the authoritative basis
        // `lag` acks behind, rebuilt through the wire. ackSeq = t - lag; the state that has folded command (t - lag) is
        // contStates[t - lag + 1]; pending = seqs (t - lag + 1 .. t) = `lag` commands replayed on top of the basis.
        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(seed);
        var recon = new List<Frame>();
        for (int t = 0; t < ticks; t++)
        {
            pred.Predict(cmd);   // returns seq t
            if (t >= lag)
            {
                int ackSeq = t - lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                MovementState wire = MovementState.From(authFull);                 // quantizes ClimbRate, drops the EWMA
                PlayerMoveState basis = PlayerMoveState.From(authFull.Position, wire);
                pred.Reconcile(t, basis, ackSeq);
            }
            PlayerMoveState ps = pred.PredictedState;
            recon.Add(new Frame(ps.Position, ps.Grounded, ps.VerticalVelocity, ps.Move.ClimbRate));
        }
        return (cont, recon);
    }

    static ReplicatedCharacterAnimators NewAnimators()
    {
        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        return new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default), CharacterAnimatorTuning.Default);
    }

    // Present the reconciled per-tick stream to the real animator (tick-aligned, render == sim tick); the constant
    // sink is a DC offset an inter-tick lerp would not remove, so tick-aligned is the honest measure.
    static float[] RenderY(List<Frame> frames, float speed)
    {
        var a = NewAnimators();
        var y = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            a.Update(new[] { new CharacterSample(1, frames[i].Pos, isLocal: true, grounded: frames[i].Grounded,
                verticalVelocity: frames[i].VVel, planarSpeed: speed, swimming: false, climbRate: frames[i].ClimbRate) }, Dt);
            y[i] = a.Live[0].RenderPosition.Y;
        }
        return y;
    }

    // The steady middle of the climb (warm-up start and crest excluded), by the CONTINUOUS ClimbRate engage/peak.
    static (int lo, int hi, int engage, int peak) SteadyWindow(List<Frame> cont)
    {
        int peak = 0; float peakY = -1e9f;
        for (int i = 0; i < cont.Count; i++) if (cont[i].Pos.Y > peakY) { peakY = cont[i].Pos.Y; peak = i; }
        int engage = 0;
        for (int i = 0; i < cont.Count; i++) if (cont[i].ClimbRate != 0f) { engage = i; break; }
        int span = peak - engage;
        int lo = engage + span / 4, hi = peak - span / 12;
        return (lo, hi, engage, peak);
    }

    // Render offset of a presented stream vs the true feet (the continuous position `cont`, which the reconciled
    // replay reproduces exactly). Two honest metrics, matching StairGlideEquilibriumTests: the MEAN offset, and the
    // most-negative BOB-REMOVED moving average (~1-riser window) - the SUSTAINED sink. The per-frame instantaneous
    // min is deliberately NOT used: the per-riser bob dips ~38 mm below true even in the lag-free continuous stream,
    // so an instantaneous bar would flag legitimate bob, not the reconcile artifact.
    static (float mean, float sustainedMin) RenderOffsetStats(float[] r, List<Frame> cont, int lo, int hi)
    {
        double sum = 0; int n = 0;
        for (int i = lo; i < hi; i++) { sum += r[i] - cont[i].Pos.Y; n++; }
        const int win = 4;   // ~1-riser window (same as the equilibrium test's bob-removal), removes the per-riser bob
        float sustainedMin = 1e9f;
        for (int i = lo; i < hi; i++)
        {
            float mv = 0f; int c = 0;
            for (int k = i - win; k <= i + win; k++) if (k >= lo && k < hi) { mv += r[k] - cont[k].Pos.Y; c++; }
            sustainedMin = MathF.Min(sustainedMin, mv / c);
        }
        return ((float)(sum / n), sustainedMin);
    }

    public static IEnumerable<object[]> WalkAndRun()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

    // (1) THE RECONCILE PARITY TEST. At a realistic ack lag, the local replayed ClimbRate must track the continuous
    // stream within one wire quantum, and the render equilibrium must stay inside the StairGlideEquilibriumTests bars
    // (|mean| < 30 mm, no sink beyond -30 mm). RED on current code: the EWMA restarts every reconcile so the signal
    // reads below achieved and the render sinks (~100 mm at run). GREEN once From seeds the EWMA from the wire rate.
    [Theory]
    [MemberData(nameof(WalkAndRun))]
    public void ReconciledClimbRate_TracksContinuous_AndRenderDoesNotSink(bool run)
    {
        const int Lag = 4;   // ~130 ms RTT at 30 Hz: a realistic short window, shorter than the tau = 6-tick EWMA
        var (cont, recon) = DriveContinuousAndReconciled(run, Lag);
        var (lo, hi, engage, peak) = SteadyWindow(cont);
        Assert.True(peak - engage > 30, "degenerate climb window");
        float speed = run ? Run : Walk;
        string tag = run ? "run" : "walk";

        // Harness validity: matching physics -> the reconciled replay reproduces the continuous position exactly, so
        // the ONLY difference the ClimbRate comparison can see is the EWMA restart (never a position divergence).
        float maxPosErr = 0f;
        for (int i = lo; i < hi; i++) maxPosErr = MathF.Max(maxPosErr, MathF.Abs(recon[i].Pos.Y - cont[i].Pos.Y));
        Assert.True(maxPosErr < 1e-3f, $"{tag}: harness invalid - reconciled pos diverged {maxPosErr * 1000:F2} mm from continuous");

        // ClimbRate parity: the local replayed signal within one quantum of the continuous (converged) signal.
        float maxRateGap = 0f;
        for (int i = lo; i < hi; i++) maxRateGap = MathF.Max(maxRateGap, MathF.Abs(recon[i].ClimbRate - cont[i].ClimbRate));

        // Render equilibrium: the RECONCILED stream vs the true feet, and the lag-free CONTINUOUS stream as the
        // baseline (the reconcile must not degrade the glide below what a lag-free client already produces).
        var (rMean, rSink) = RenderOffsetStats(RenderY(recon, speed), cont, lo, hi);
        var (cMean, cSink) = RenderOffsetStats(RenderY(cont, speed), cont, lo, hi);

        string metrics = $"{tag} (lag {Lag}): rateGap={maxRateGap * 1000:F1} mm/s (bar {Quantum * 1000:F0}) | " +
                         $"recon mean={rMean * 1000:F1} sustainedSink={rSink * 1000:F1} mm; " +
                         $"lag-free baseline mean={cMean * 1000:F1} sustainedSink={cSink * 1000:F1} mm (bars +/-30, sink -30)";

        Assert.True(maxRateGap <= Quantum, metrics + " - reconciled ClimbRate gap exceeds one wire quantum");
        Assert.True(MathF.Abs(rMean) < 0.03f, metrics + " - reconciled render mean offset exceeds 30 mm");
        Assert.True(rSink > -0.03f, metrics + " - reconciled render sustained-sinks below -30 mm (the EWMA-restart sink)");
        // Parity: the reconcile adds no meaningful sustained sink vs the lag-free client (within 10 mm on both metrics).
        Assert.True(rMean - cMean > -0.010f, metrics + " - reconcile drags the mean > 10 mm below lag-free");
        Assert.True(rSink - cSink > -0.010f, metrics + " - reconcile deepens the sustained sink > 10 mm below lag-free");
    }

    // (2) MID-CLIMB CREST-EASE FLIP GUARD. Across the reconciles, ClimbRate must never read exactly 0 on a genuinely
    // climbing tick: a 0 flips the animator's `climbing` gate off mid-climb, hard-cutting into the crest-ease/raw
    // branch (the finding-2 cosmetic). On current code the ==0 re-seed sentinel fires every reconcile and a basis tick
    // with appliedRate ~ 0 can seed the EWMA to 0. GREEN once the wire-rate seed keeps it positive through the run.
    [Theory]
    [MemberData(nameof(WalkAndRun))]
    public void ReconciledClimbRate_NeverReadsZeroMidClimb(bool run)
    {
        const int Lag = 4;
        var (cont, recon) = DriveContinuousAndReconciled(run, Lag);
        var (lo, hi, _, _) = SteadyWindow(cont);
        string tag = run ? "run" : "walk";

        int zeros = 0, firstZero = -1;
        for (int i = lo; i < hi; i++)
        {
            // A genuinely climbing tick = the continuous (authoritative) signal is climbing there. The reconciled
            // signal must not have dropped to 0 on it.
            if (cont[i].ClimbRate > 0f && recon[i].ClimbRate == 0f) { zeros++; if (firstZero < 0) firstZero = i; }
        }
        Assert.True(zeros == 0,
            $"{tag}: reconciled ClimbRate read 0 on {zeros} climbing tick(s) (first at index {firstZero}) - flips the crest-ease branch mid-climb");
    }
}
