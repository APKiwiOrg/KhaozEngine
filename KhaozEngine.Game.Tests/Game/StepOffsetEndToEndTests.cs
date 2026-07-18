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

// E5 end-to-end: drive the REAL CharacterMovement.Step over Bepu geometry through the REAL ClientPrediction into the REAL
// ReplicatedCharacterAnimators, and pin the whole chain on an isolated building doorstep: the SIM commits the step (its
// authoritative feet pop up), the mesh EASES (the drawn feet do not pop), the authoritative motion is UNCHANGED, and the
// ease is applied EXACTLY ONCE even under reconciliation. This is the engine mirror of the consumer doorstep-popup probe.
public class StepOffsetEndToEndTests
{
    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = 0.4f };
    const float Dt = 1f / 30f;

    // A wrapper making CharacterMovement.Step an ITickSimulator so ClientPrediction can predict/replay it (the same shape
    // the game heads use). Deterministic over a fixed Bepu world.
    sealed class PlayerStepSim : ITickSimulator<PlayerMoveState, MoveCommand>
    {
        readonly IPhysicsWorld _world; readonly MoveTuning _t;
        readonly Func<float, float, float> _ground; readonly Func<float, float, Vector3> _normal;
        public PlayerStepSim(IPhysicsWorld w, MoveTuning t, Func<float, float, float> g, Func<float, float, Vector3> n)
        { _world = w; _t = t; _ground = g; _normal = n; }
        public PlayerMoveState Step(in PlayerMoveState s, in MoveCommand cmd, float dt)
            => new PlayerMoveState { Move = CharacterMovement.Step(s.Move, cmd, dt, _ground, _t, _normal, _world), TeleportEpoch = s.TeleportEpoch };
    }

    static IPhysicsWorld Doorstep(float h)
    {
        IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(20f, h * 0.5f, 6f)), Pose.At(new Vector3(0f, h * 0.5f, -6f)));
        world.Step(Dt);
        return world;
    }

    static ReplicatedCharacterAnimators NewBridge(bool stepSmoothing)
    {
        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        var tuning = CharacterAnimatorTuning.Default;
        if (!stepSmoothing) tuning.StepSmoothingRate = 0f;   // disabled = the raw popup baseline this feature fixes
        return new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default), tuning);
    }

    const int RenderPerTick = 2;                 // render at 60 fps over a 30 Hz sim (the realistic ratio)
    const float RenderDt = Dt / RenderPerTick;

    // Run the doorstep approach for `ticks` sim ticks, rendering RenderPerTick frames per tick (inter-tick interpolated).
    // When `reconcile` is set, interleave a MATCHING reconcile every 2 ticks (to the client's own state 3 ticks back - a
    // perfect single-player prediction, so no snap) to prove reconciles do not perturb the mesh offset. Returns per-render-
    // frame (sim feet Y at that tick, drawn feet Y).
    static (List<float> simY, List<float> drawnY) Run(int ticks, bool stepSmoothing, bool reconcile)
    {
        MoveTuning t = Tuning(); float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = Doorstep(0.25f);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var sim = new PlayerStepSim(world, t, Ground, normal);
        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        prediction.Reset(new PlayerMoveState { Move = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true } });
        var bridge = NewBridge(stepSmoothing);
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);   // forward -Z onto the doorstep

        var history = new List<PlayerMoveState>();
        var simY = new List<float>(); var drawnY = new List<float>();
        for (int i = 0; i < ticks; i++)
        {
            if (reconcile && i > 0 && i % 2 == 0 && history.Count > 3)
            {
                int ackSeq = history.Count - 1 - 3;
                prediction.Reconcile(i, history[ackSeq], ackSeq);          // matching basis -> no snap
            }
            prediction.Predict(cmd);
            history.Add(prediction.PredictedState);
            float simFeet = prediction.PredictedState.Move.Position.Y;     // authoritative feet this tick (raw)
            for (int f = 0; f < RenderPerTick; f++)
            {
                prediction.AdvancePresentation(RenderDt);
                PlayerMoveState rs = prediction.RenderedState;
                var sample = new CharacterSample(1, rs.Position, isLocal: true, grounded: rs.Grounded,
                    verticalVelocity: rs.VerticalVelocity, planarSpeed: prediction.PredictedHorizontalSpeed,
                    swimming: rs.Swimming, climbRate: rs.Move.ClimbRate, stepCumulativeY: prediction.StepCumulativeY);
                bridge.Update(new[] { sample }, RenderDt);
                simY.Add(simFeet);
                drawnY.Add(bridge.Live[0].RenderPosition.Y);
            }
        }
        return (simY, drawnY);
    }

    [Fact]
    public void Doorstep_SimIsUnchanged_ByThePresentationLayer()
    {
        // The authoritative sim must be byte-identical whether the presentation step-smoothing is on or off (it is a pure
        // OUTPUT: StepDeltaY never feeds back into position). Compare the predicted feet stream to the SAME sim run
        // directly (no prediction, no bridge) - they match tick for tick, so prediction + the mesh layer leave the
        // authoritative motion exactly as it was.
        MoveTuning t = Tuning(); float halfH = t.CapsuleHalfHeight;
        (List<float> simY, List<float> _) = Run(ticks: 40, stepSmoothing: true, reconcile: false);
        using IPhysicsWorld world = Doorstep(0.25f);
        float Ground(float x, float z) => 0f; Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var refState = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        // simY has RenderPerTick entries per tick (the same value repeated); step the reference once per tick and compare.
        for (int i = 0; i < simY.Count; i += RenderPerTick)
        {
            refState = CharacterMovement.Step(refState, cmd, Dt, Ground, t, normal, world);
            Assert.Equal(BitConverter.SingleToInt32Bits(refState.Position.Y), BitConverter.SingleToInt32Bits(simY[i]));
        }
    }

    [Fact]
    public void Doorstep_MeshEases_NoPop_VersusTheRawBaseline()
    {
        // Enabled vs disabled (disabled == the raw popup this feature fixes). The step-smoothed mesh must LAG the
        // authoritative feet by a meaningful fraction of the step during the mount (it held back instead of popping),
        // materially MORE than the raw baseline, then converge onto the true feet. A pop would show ~0 extra lag.
        (List<float> simY, List<float> drawnOn) = Run(ticks: 40, stepSmoothing: true, reconcile: false);
        (List<float> _, List<float> drawnOff) = Run(ticks: 40, stepSmoothing: false, reconcile: false);

        float maxLagOn = 0f, maxLagOff = 0f;
        for (int i = 0; i < simY.Count; i++)
        {
            maxLagOn = MathF.Max(maxLagOn, simY[i] - drawnOn[i]);
            maxLagOff = MathF.Max(maxLagOff, simY[i] - drawnOff[i]);
            Assert.True(drawnOn[i] <= simY[i] + 0.01f, $"frame {i}: the mesh led the sim feet ({drawnOn[i]:F3} > {simY[i]:F3}) - it should lag, not lead");
        }
        // The step-smoothed mesh holds back ~1/3+ of the 0.25 m step at its peak; the raw baseline barely lags (interp only).
        Assert.True(maxLagOn > 0.08f, $"the step-smoothed mesh should lag the sim feet by >80 mm at its peak (got {maxLagOn * 1000f:F0} mm) - it eased, not popped");
        Assert.True(maxLagOn > maxLagOff + 0.05f, $"the step-smoothed mesh ({maxLagOn * 1000f:F0} mm lag) must ease materially more than the raw baseline ({maxLagOff * 1000f:F0} mm)");
        // Both settle onto the true feet by the end (the ease converges; no residual offset).
        Assert.True(MathF.Abs(drawnOn[^1] - simY[^1]) < 0.01f, $"the drawn feet must settle onto the sim feet ({drawnOn[^1]:F3} vs {simY[^1]:F3})");
    }

    [Fact]
    public void Doorstep_WithReconciles_MeshTrajectoryMatchesNoReconcile_ExactlyOnce()
    {
        // The mesh ease must be applied EXACTLY ONCE through the real prediction: reconciles that replay the pending window
        // across the paced step ticks must not re-add the offset. Compare the drawn-feet trajectory WITH heavy
        // reconciliation to the one WITHOUT - they must match (a double-consuming bridge would ease more, a lower trajectory).
        (List<float> _, List<float> drawnNoRec) = Run(ticks: 40, stepSmoothing: true, reconcile: false);
        (List<float> _, List<float> drawnRec) = Run(ticks: 40, stepSmoothing: true, reconcile: true);
        Assert.Equal(drawnNoRec.Count, drawnRec.Count);
        for (int i = 0; i < drawnNoRec.Count; i++)
            Assert.True(MathF.Abs(drawnNoRec[i] - drawnRec[i]) < 1e-3f,
                $"frame {i}: reconciliation changed the mesh ease ({drawnNoRec[i]:F4} vs {drawnRec[i]:F4}) - the offset was not consumed exactly once");
    }
}
