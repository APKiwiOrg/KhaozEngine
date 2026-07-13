using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// E1 (signal-driven stair glide): CharacterMovement.Step exports a signed step-climb rate (MoveState.ClimbRate) that is
// the SINGLE source of truth for "am I climbing" the presentation smoother reads - no more estimating climb state from
// position deltas. These pin the sign of the signal on the real Bepu box staircase (positive through a continuous
// ascent, negative through a stepped descent, exactly 0 on flat / jump / fall / a lone single-riser seat) and the wire
// quantization round-trip (MovementState.ClimbRateQ).
public class ClimbSignalTests
{
    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = 0.4f };

    // A solid-box staircase climbing in -Z, approached head-on from +Z (yaw 0 => forward -Z). Mirrors
    // StairRunTangentPacingTests' fixture (grade riser/tread).
    static void AddStairs(IPhysicsWorld world, float riser, float tread, int risers)
    {
        float backZ = -tread * risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < risers; i++)
        {
            float frontZ = -tread * i;
            float treadTop = riser * (i + 1);
            float centerZ = 0.5f * (frontZ + backZ);
            float depth = frontZ - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    const float Riser = 0.30f, Tread = 0.40f;
    const float Dt = 1f / 30f;

    // ---- Ascent: ClimbRate is positive through a continuous run, never negative ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContinuousAscent_StampsPositiveClimbRate_NeverNegative(bool run)
    {
        MoveTuning t = Tuning();
        int risers = 33;
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world, Riser, Tread, risers);
        world.Step(Dt);

        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        float speed = run ? t.RunSpeed : t.WalkSpeed;
        int ticks = (int)(1.6f * (Tread * risers + 3f) / (0.5f * speed * Dt));
        float loY = halfH + 2f * Riser + 0.05f, hiY = Riser * risers + halfH - 0.05f;
        int positive = 0, negative = 0, climbSamples = 0;
        for (int i = 0; i < ticks; i++)
        {
            bool onRamp = state.Position.Y > loY && state.Position.Y < hiY;
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            if (onRamp && state.Grounded)
            {
                climbSamples++;
                if (state.ClimbRate > 0f) positive++;
                if (state.ClimbRate < 0f) negative++;
            }
        }

        Assert.True(climbSamples > 20, $"run={run}: too few climb samples ({climbSamples})");
        // The ascent stamps a POSITIVE rate (the paced cap), and NEVER a negative one (that would be a descent).
        Assert.True(positive > 0, $"run={run}: no positive ClimbRate stamped through the ascent");
        Assert.True(negative == 0, $"run={run}: {negative} negative ClimbRate ticks during an ascent (should be a descent-only sign)");
        // The stamped rate is exactly the paced cap where it fires (the honest grade-limited vertical rate).
    }

    [Fact]
    public void AscentClimbRate_SaturatesMaxStepClimbSpeed_OnARun_NeverExceedsIt()
    {
        // The ascent climb rate is the honest even rate min(commandedForward * grade, MaxStepClimbSpeed): a RUN (6 m/s)
        // on a 0.30/0.40 stair (grade 0.75) wants 4.5 m/s, so it SATURATES the MaxStepClimbSpeed cap (3.5) and never
        // exceeds it - the paced ceiling. The signal is the vertical rate the render glide feeds forward.
        MoveTuning t = Tuning();
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world, Riser, Tread, 33);
        world.Step(Dt);
        var state = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        float maxSeen = 0f;
        for (int i = 0; i < 200; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            Assert.True(state.ClimbRate <= t.MaxStepClimbSpeed + 1e-4f,
                $"ascent ClimbRate {state.ClimbRate} exceeded the MaxStepClimbSpeed cap {t.MaxStepClimbSpeed}");
            maxSeen = MathF.Max(maxSeen, state.ClimbRate);
        }
        Assert.True(maxSeen >= t.MaxStepClimbSpeed - 0.1f,
            $"a run up the stair should saturate the paced cap {t.MaxStepClimbSpeed}, but peaked at {maxSeen}");
    }

    // ---- Descent: stepping off a raised platform stamps a negative rate ----
    [Fact]
    public void SteppedDescent_StampsNegativeClimbRate_NeverPositive()
    {
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // A platform whose top is a 0.35 m step above ground (within StepHeight 0.4), front edge at Z=0, extending -Z.
        const float top = 0.35f;
        world.AddStatic(new BoxShape(new Vector3(20f, top * 0.5f, 6f)), Pose.At(new Vector3(0f, top * 0.5f, -6f)));
        world.Step(Dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        // Start on the platform, walk +Z (Move.Y = -1 => forward +Z) toward and off the front edge.
        var state = new MoveState { Position = new Vector3(0f, top + halfH, -1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        int negative = 0, positive = 0;
        bool sawStepDown = false;
        for (int i = 0; i < 60; i++)
        {
            float yBefore = state.Position.Y;
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            if (state.ClimbRate < 0f) { negative++; sawStepDown = true; }
            if (state.ClimbRate > 0f) positive++;
        }
        Assert.True(sawStepDown, "expected the step-down grounded-hold to stamp a negative ClimbRate");
        Assert.True(positive == 0, $"a descent stamped {positive} positive (ascending) ClimbRate ticks");
        // The magnitude is clamped to the paced descent rate.
    }

    [Fact]
    public void SteppedDescent_ClampsToMaxStepClimbSpeed()
    {
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        const float top = 0.35f;
        world.AddStatic(new BoxShape(new Vector3(20f, top * 0.5f, 6f)), Pose.At(new Vector3(0f, top * 0.5f, -6f)));
        world.Step(Dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, top + halfH, -1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            // -(stepDrop/dt) with stepDrop 0.35 and dt 1/30 is -10.5, clamped to -MaxStepClimbSpeed.
            Assert.True(state.ClimbRate >= -t.MaxStepClimbSpeed - 1e-4f,
                $"descent ClimbRate {state.ClimbRate} exceeded the -MaxStepClimbSpeed clamp");
        }
    }

    // ---- Zero cases: flat, jump, fall, a lone single-riser seat ----
    [Fact]
    public void FlatGround_StampsZeroClimbRate()
    {
        MoveTuning t = Tuning();
        var state = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        for (int i = 0; i < 60; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t);
            Assert.Equal(0f, state.ClimbRate, 5);
        }
    }

    [Fact]
    public void JumpAndFall_StampZeroClimbRate()
    {
        MoveTuning t = Tuning();
        var state = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float Ground(float x, float z) => 0f;
        // Jump.
        var jump = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: true);
        state = CharacterMovement.Step(state, jump, Dt, Ground, t);
        Assert.Equal(0f, state.ClimbRate, 5);
        // Airborne rise + fall: ClimbRate stays 0 the whole arc (a fall is never a climb - the fall-sink guard).
        var idle = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Ground, t);
            Assert.Equal(0f, state.ClimbRate, 5);
        }
    }

    [Fact]
    public void BallisticFall_FromHeight_NeverStampsClimbRate()
    {
        // The exact prod bug's root: a ballistic fall must never read as a climb (the fall-sink). Drop from well above
        // the floor and assert ClimbRate is 0 on every tick, including the landing tick.
        MoveTuning t = Tuning();
        var state = new MoveState { Position = new Vector3(0f, 5f + t.CapsuleHalfHeight, 0f), Grounded = false, VerticalVelocity = 0f };
        var idle = new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Ground, t);
            Assert.Equal(0f, state.ClimbRate, 5);
        }
        Assert.True(state.Grounded, "should have landed");
    }

    // A one-sided single riser (a +Z-facing riser quad + a +Y-facing deep tread quad), the canonical single-riser
    // fixture from SingleRiserMountTests: the one-sided face is what makes the mount realistic (a solid box mounts
    // trivially). A lone riser onto a deep tread is a one-off SEAT, not a continuous run - the mount commits the
    // landed seat onto the deep tread, so NextRiserAhead reads clear and the co-pace never stamps a glide signal.
    static TriangleMeshShape OneSidedStep(float riserHeight, float treadDepth = 40f, float halfX = 20f)
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        void Tri(int a, int b, int c) { idx.Add(a); idx.Add(b); idx.Add(c); }
        int b0 = v.Count;
        v.Add(new Vector3(-halfX, 0f, 0f)); v.Add(new Vector3(halfX, 0f, 0f));
        v.Add(new Vector3(halfX, riserHeight, 0f)); v.Add(new Vector3(-halfX, riserHeight, 0f));
        Tri(b0 + 0, b0 + 2, b0 + 1); Tri(b0 + 0, b0 + 3, b0 + 2);
        b0 = v.Count;
        v.Add(new Vector3(-halfX, riserHeight, 0f)); v.Add(new Vector3(halfX, riserHeight, 0f));
        v.Add(new Vector3(halfX, riserHeight, -treadDepth)); v.Add(new Vector3(-halfX, riserHeight, -treadDepth));
        Tri(b0 + 0, b0 + 1, b0 + 2); Tri(b0 + 0, b0 + 2, b0 + 3);
        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }

    [Fact]
    public void SingleRiserSeat_StampsZeroClimbRate()
    {
        // A lone riser onto a deep tread is a one-off mount, NOT a continuous run (no mountable riser ahead once
        // seated), so it must NOT stamp an ascent signal - the smoother should render it raw, not glide it.
        var t = MoveTuning.Default;   // the shipped tuning the single-riser mount pins use (radius 0.4, walk 6)
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(OneSidedStep(0.30f), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        int positive = 0;
        for (int i = 0; i < 180; i++)
        {
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Ground, t, normal, world);
            if (state.ClimbRate > 0f) positive++;
        }
        Assert.True(state.Position.Y > 0.30f + halfH - 0.05f, $"should have mounted the single riser (Y {state.Position.Y:F3})");
        Assert.True(positive == 0, $"a single-riser seat stamped {positive} positive ClimbRate ticks (should be a raw mount, not a glide)");
    }

    // ---- Wire quantization round-trip (MovementState.ClimbRateQ) ----
    [Theory]
    [InlineData(0f)]
    [InlineData(3.5f)]
    [InlineData(-3.5f)]
    [InlineData(2.25f)]
    [InlineData(-1.34f)]
    public void ClimbRate_Quantization_RoundTripsWithinOneQuantum(float rate)
    {
        sbyte q = MovementState.QuantizeClimbRate(rate);
        float decoded = MovementState.DecodeClimbRate(q);
        Assert.True(MathF.Abs(decoded - rate) <= MovementState.ClimbRateQuantum,
            $"rate {rate} -> q {q} -> {decoded} differs by more than one quantum {MovementState.ClimbRateQuantum}");
    }

    [Fact]
    public void SubQuantumClimbRate_DecodesToZero()
    {
        // A climb slower than half a quantum is below perception (sub-millimetre per frame) and quantizes to 0 - the
        // implicit not-climbing dead-zone, exactly the gate the deleted estimator's MinGradeForGlide used to carve out.
        foreach (float tiny in new[] { 0.02f, -0.02f, 0.0249f, -0.0249f })
            Assert.Equal(0, MovementState.QuantizeClimbRate(tiny));
    }

    [Fact]
    public void QuantizeClimbRate_ClampsToSbyteRange()
    {
        Assert.Equal((sbyte)127, MovementState.QuantizeClimbRate(1000f));
        Assert.Equal((sbyte)(-127), MovementState.QuantizeClimbRate(-1000f));
    }
}
