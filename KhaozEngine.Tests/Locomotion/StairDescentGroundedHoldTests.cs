using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Step-DOWN grounded-hold (CharacterMovement step 4a-down). Walking OFF a step whose drop is within StepHeight (0.40) is
// a step, not a fall: it must stay grounded and seat onto the support one riser below. Before the fix the grounded stick
// only reached GroundedEpsilon (0.30) below the feet, so a door-step-sized drop between GroundedEpsilon and StepHeight
// (measured: it starts flicking at ~0.38 m, e.g. a ~0.40 m step) slipped past: `grounded` flipped false and gravity
// spiked the vertical velocity to ~3.3 m/s over the next few ticks - past the render smoother's 2.0 m/s ballistic
// threshold - so the drawn height hard-cut and flapped ("very glitchy going down" on those steps). The fix holds the
// descent grounded up to StepHeight and seats it in one tick (like a within-GroundedEpsilon step-down already does); a
// genuine ledge walk-off beyond StepHeight still falls (the ledge-release invariant). Real Bepu geometry, GPU-free.
public class StairDescentGroundedHoldTests
{
    const float BallisticVerticalSpeed = 2.0f;   // mirrors ReplicatedCharacterAnimators; above this the smoother snaps
    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = 0.4f };

    readonly struct Tick
    {
        public Tick(Vector3 p, bool g, float v, float cr) { Pos = p; Grounded = g; VVel = v; ClimbRate = cr; }
        public Vector3 Pos { get; }
        public bool Grounded { get; }
        public float VVel { get; }
        public float ClimbRate { get; }   // the sim's exported step-climb signal (E1), driving the signal-gated glide
    }

    // Isolated single step-down of height `riser`: an upper box (top at `riser`) spanning Z in [-12, 0], analytic terrain
    // at y=0 for Z>0. Walk +Z off the front edge onto the terrain (the door-step case). Per-tick capsule state.
    static List<Tick> StepDown(float riser)
    {
        MoveTuning t = Tuning(); float halfH = t.CapsuleHalfHeight, dt = 1f / 30f;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(20f, riser * 0.5f, 6f)), Pose.At(new Vector3(0f, riser * 0.5f, -6f)));
        world.Step(dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, riser + halfH, -2f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // forward +Z
        var outp = new List<Tick>();
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, cmd, dt, Ground, t, normal, world);
            outp.Add(new Tick(state.Position, state.Grounded, state.VerticalVelocity, state.ClimbRate));
        }
        return outp;
    }

    // A step-down WITHIN StepHeight holds grounded the whole way and never spikes the vertical velocity past the
    // smoother's ballistic threshold - so the render height glides the drop instead of hard-cutting a ballistic fall.
    // RED before the fix at 0.38 / 0.40 (they flicked airborne with vVel ~-3.33). 0.32 held even before, kept as a guard.
    [Theory]
    [InlineData(0.32f)]
    [InlineData(0.38f)]
    [InlineData(0.40f)]
    public void StepDownWithinStepHeight_HoldsGrounded_NoVvelSpike(float riser)
    {
        List<Tick> p = StepDown(riser);
        // It actually descended onto the terrain and settled there (a real step-down, not a no-op).
        Assert.True(MathF.Abs(p[^1].Pos.Y - 0.9f) < 0.02f, $"riser {riser}: did not settle on the terrain (final Y {p[^1].Pos.Y:F3})");
        Assert.True(p[^1].Grounded, $"riser {riser}: not grounded at the end");

        // Never airborne, and the vertical velocity never spikes ballistic (the smoother would hard-cut on that).
        float worstDown = 0f; int airborne = 0;
        foreach (Tick tk in p) { if (!tk.Grounded) airborne++; worstDown = MathF.Min(worstDown, tk.VVel); }
        Assert.True(airborne == 0, $"riser {riser}: went airborne on {airborne} tick(s) - the step-down flick");
        Assert.True(worstDown > -BallisticVerticalSpeed,
            $"riser {riser}: vertical velocity spiked to {worstDown:F2} m/s (past the {BallisticVerticalSpeed} ballistic threshold) - the descent flap");
    }

    // A drop BEYOND StepHeight is a genuine ledge walk-off: it MUST still release and fall (grounded-hold must not
    // over-hold). Pairs with GroundedCapsule_WalksOffLedge_ReleasesAndFalls (a 3 m ledge) in ControllerOnPhysicsTests.
    [Theory]
    [InlineData(0.45f)]
    [InlineData(0.60f)]
    public void StepDownBeyondStepHeight_StillReleasesAndFalls(float riser)
    {
        List<Tick> p = StepDown(riser);
        bool wentAirborne = false; float worstDown = 0f;
        foreach (Tick tk in p) { if (!tk.Grounded) wentAirborne = true; worstDown = MathF.Min(worstDown, tk.VVel); }
        Assert.True(wentAirborne, $"riser {riser} (> StepHeight): should walk off as a ledge and go airborne, but never did");
        Assert.True(worstDown < -BallisticVerticalSpeed, $"riser {riser}: a real ledge fall should build a ballistic vertical velocity (got {worstDown:F2})");
        // Still lands on the terrain by the end (it is a fall, not a launch into space).
        Assert.True(MathF.Abs(p[^1].Pos.Y - 0.9f) < 0.05f, $"riser {riser}: did not land on the terrain (final Y {p[^1].Pos.Y:F3})");
    }

    // End-to-end: feed the 0.40 m step-down through the SIGNAL-GATED render glide at 120 fps. Assert the render never
    // SINKS below the true feet - the fall-sink guarantee holds for a grounded step-down too. An ISOLATED step-down is not
    // a continuous run, so the sim leaves ClimbRate 0 (it exports the DISCRETE-STEP impulse StepDeltaY instead, which the
    // separate mesh-offset layer eases - see StepOffsetSmoothingTests). With ClimbRate 0 the continuous glide renders the
    // drop RAW (no glide, nothing carried past the floor), so the render tracks the true feet exactly and cannot sink. A
    // CONTINUOUS stepped descent (many risers) DOES glide smoothly on the continuous ClimbRate signal, pinned by
    // StairGlideRealisticStreamTests' down cases. This sample does not feed StepCumulativeY, so the mesh-offset layer is
    // inert here; it pins the property that matters: the drawn feet never dip BELOW the true feet on the way down.
    [Fact]
    public void StepDown_0p40_RenderNeverSinksBelowTrue_At120fps()
    {
        List<Tick> ticks = StepDown(0.40f);
        var frames = new List<Tick> { ticks[0] };
        for (int k = 1; k < ticks.Count; k++)
            for (int f = 1; f <= 4; f++)
                frames.Add(new Tick(Vector3.Lerp(ticks[k - 1].Pos, ticks[k].Pos, f / 4f), ticks[k].Grounded, ticks[k].VVel, ticks[k].ClimbRate));

        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        var a = new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default), CharacterAnimatorTuning.Default);

        float dtR = (1f / 30f) / 4f, worstSink = 0f;
        foreach (Tick fr in frames)
        {
            a.Update(new[] { new CharacterSample(1, fr.Pos, isLocal: true, grounded: fr.Grounded, verticalVelocity: fr.VVel, planarSpeed: 3f, swimming: false, climbRate: fr.ClimbRate) }, dtR);
            float renderY = a.Live[0].RenderPosition.Y;
            worstSink = MathF.Min(worstSink, renderY - fr.Pos.Y);   // how far the drawn feet went BELOW the true feet
        }
        // The drawn feet never dip below the true feet (the render glides on/above the true drop then settles onto the
        // true tread; the negative signal never carries the feet down past the floor - no under-floor sink).
        Assert.True(worstSink > -0.01f,
            $"the render sank {worstSink * 1000:F0} mm below the true feet during the step-down (a downward under-floor pop)");
    }
}
