using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// E5 (UE-style step-event mesh smoothing): CharacterMovement.Step exports the DISCRETE-STEP impulse MoveState.StepDeltaY -
// the signed vertical delta an ISOLATED step commits this tick, a step the CONTINUOUS climb signal (ClimbRate) declines.
// It is MUTUALLY EXCLUSIVE with ClimbRate per tick: a continuous run exports ClimbRate and leaves StepDeltaY 0 (the glide
// owns the smoothing); an isolated step (doorstep / curb / first riser / isolated step-down) exports StepDeltaY and leaves
// ClimbRate 0 (the glide renders raw and a decaying mesh offset eases the step). These pin the sign + the mutual exclusion
// on real Bepu geometry, and the zero cases (flat / jump / fall / continuous run).
public class StepDeltaExportTests
{
    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = 0.4f };
    const float Dt = 1f / 30f;

    // A solid-box doorstep/curb of height `h` (top flat), front face at Z=0, extending -Z. Analytic terrain y=0 for Z>0.
    static IPhysicsWorld Doorstep(float h)
    {
        IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(20f, h * 0.5f, 6f)), Pose.At(new Vector3(0f, h * 0.5f, -6f)));
        world.Step(Dt);
        return world;
    }

    // A solid-box staircase climbing in -Z (mirrors ClimbSignalTests.AddStairs): a CONTINUOUS run.
    static void AddStairs(IPhysicsWorld world, float riser, float tread, int risers)
    {
        float backZ = -tread * risers - 2f;
        for (int i = 0; i < risers; i++)
        {
            float treadTop = riser * (i + 1);
            float centerZ = 0.5f * (-tread * i + backZ);
            float depth = -tread * i - backZ;
            world.AddStatic(new BoxShape(new Vector3(20f, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    [Fact]
    public void IsolatedStepUp_StampsPositiveStepDelta_AndZeroClimbRate()
    {
        // Walk onto a lone 0.25 m doorstep (flat top beyond): an ISOLATED step-up, not a continuous run. It exports a
        // POSITIVE StepDeltaY (the committed rise, possibly over a couple paced ticks) whose SUM is the step height, and
        // NEVER a continuous ClimbRate.
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        const float step = 0.25f;
        using IPhysicsWorld world = Doorstep(step);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);   // forward -Z onto the step

        float sumUp = 0f; int climbTicks = 0; int stepUpTicks = 0;
        for (int i = 0; i < 90; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            if (state.ClimbRate != 0f) climbTicks++;
            if (state.StepDeltaY > 0f) { sumUp += state.StepDeltaY; stepUpTicks++; }
            Assert.True(state.StepDeltaY >= -1e-4f, $"a step-UP never stamps a negative StepDeltaY (got {state.StepDeltaY})");
        }
        Assert.True(state.Position.Y > step + halfH - 0.05f, $"should have mounted the doorstep (Y {state.Position.Y:F3})");
        Assert.True(stepUpTicks > 0, "expected at least one positive StepDeltaY impulse mounting the doorstep");
        Assert.True(climbTicks == 0, $"an isolated step-up stamped {climbTicks} continuous ClimbRate ticks (should ride StepDeltaY)");
        // The exported rise sums to the step height (the mesh offset must carry the whole pop): within a tolerance for the
        // final seating overshoot the paced cap leaves.
        Assert.True(MathF.Abs(sumUp - step) < 0.08f, $"summed step-up impulse {sumUp:F3} should equal the step height {step}");
    }

    [Fact]
    public void ContinuousAscentRun_StampsZeroStepDelta_ClimbRateCarriesIt()
    {
        // A CONTINUOUS run exports ClimbRate (the glide owns the smoothing), so StepDeltaY stays 0 through the run - the
        // mesh offset must NOT double-apply on top of the glide. (The FIRST riser, before the signal engages, may stamp a
        // single StepDeltaY; the invariant is that DURING the run the two are mutually exclusive.)
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world, 0.30f, 0.40f, 33);
        world.Step(Dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        float loY = halfH + 2f * 0.30f, hiY = 0.30f * 33 + halfH - 0.30f;
        int bothNonZero = 0, inRunStepDeltas = 0;
        for (int i = 0; i < 240; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            // Mutual exclusion: never both signals in the same tick.
            if (state.ClimbRate != 0f && state.StepDeltaY != 0f) bothNonZero++;
            bool onRamp = state.Position.Y > loY && state.Position.Y < hiY;
            if (onRamp && state.ClimbRate > 0f && state.StepDeltaY != 0f) inRunStepDeltas++;
        }
        Assert.True(bothNonZero == 0, $"{bothNonZero} ticks stamped BOTH ClimbRate and StepDeltaY (must be mutually exclusive)");
        Assert.True(inRunStepDeltas == 0, $"{inRunStepDeltas} mid-run ticks stamped a StepDeltaY while ClimbRate was active (double-apply)");
    }

    [Fact]
    public void Flat_Jump_Fall_StampZeroStepDelta()
    {
        MoveTuning t = Tuning();
        float Ground(float x, float z) => 0f;
        // Flat ground.
        var state = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        var walk = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
        {
            state = CharacterMovement.Step(state, walk, Dt, Ground, t);
            Assert.Equal(0f, state.StepDeltaY, 5);
        }
        // Jump + full ballistic arc: a landing is NOT a step event.
        var jump = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: true);
        state = CharacterMovement.Step(state, jump, Dt, Ground, t);
        Assert.Equal(0f, state.StepDeltaY, 5);
        var idle = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Ground, t);
            Assert.Equal(0f, state.StepDeltaY, 5);   // 0 through the whole arc INCLUDING the landing tick (no fall-sink)
        }
        Assert.True(state.Grounded, "should have landed");
    }

    [Fact]
    public void BallisticFall_OntoAStep_NeverStampsAPositiveStepUp()
    {
        // Falling onto a step must not read as a step-UP (the fall-sink guard for this layer): a fall is a net DROP, and
        // the step-up stamp is MathF.Max(0, rise), so a landing-onto-geometry tick can never inject a positive impulse.
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = Doorstep(0.25f);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        // Drop from 4 m up, directly over the doorstep top (X=0, Z=-3, on the box).
        var state = new MoveState { Position = new Vector3(0f, 4f + halfH, -3f), Grounded = false, VerticalVelocity = 0f };
        var idle = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Ground, t, normal, world);
            Assert.True(state.StepDeltaY <= 1e-4f, $"a fall/landing stamped a positive step-up impulse {state.StepDeltaY:F3} (fall-sink)");
        }
        Assert.True(state.Grounded, "should have landed on the doorstep");
    }

    [Fact]
    public void Swimming_StampsZeroStepDelta()
    {
        // Surface-swim suspends gravity/ground-snap and returns through SwimStep, which never sets StepDeltaY: a swim tick
        // is not a step. Deep water everywhere -> the character swims -> StepDeltaY stays 0 every tick.
        MoveTuning t = Tuning();
        Func<float, float, float, MovementMedium> deep = (x, z, feetY) => new MovementMedium(5f, inWater: true, 1f);
        float Ground(float x, float z) => 0f;
        var state = new MoveState { Position = new Vector3(0f, 0.6f, 0f), Swimming = true, Grounded = false };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, groundNormal: null, world: null, clampXz: null, medium: deep);
            Assert.True(state.Swimming, "should stay swimming in deep water");
            Assert.Equal(0f, state.StepDeltaY, 6);
        }
    }

    [Fact]
    public void StepDeltaStream_IsDeterministic_AcrossIdenticalRuns()
    {
        // Both heads run this exact code; StepDeltaY is deterministic float math, so two identical runs produce a
        // bit-identical stream (the guarantee prediction and the server agree, and reconcile replay is exact).
        static float[] Run()
        {
            MoveTuning t = Tuning();
            using IPhysicsWorld world = Doorstep(0.25f);
            float Ground(float x, float z) => 0f;
            Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
            var state = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 1.0f), Grounded = true };
            var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
            var sd = new float[90];
            for (int i = 0; i < 90; i++) { state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world); sd[i] = state.StepDeltaY; }
            return sd;
        }
        float[] a = Run(), b = Run();
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(a[i]), BitConverter.SingleToInt32Bits(b[i]));
    }
}
