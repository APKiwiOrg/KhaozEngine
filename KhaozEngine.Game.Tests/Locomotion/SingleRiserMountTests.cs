using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// A discrete single riser - one hand-placed static step on flat analytic ground - is the engine-side stand-in for
// the Ruinborne building-entrance risers that surfaced the slow-walk mount stall the user hit in play: walking
// head-on into the step at walk speed, the paced step-up's capped forward advance sat below the depenetration
// pushback, so the capsule rose a little, lost the tread, fell back, and buzzed at flat height forever instead of
// climbing. These drive CharacterMovement.Step against hand-built Bepu geometry (no dungeon generation, no seed
// scan) and pin the geometry-robust monotone mount.
//
// The step is a ONE-SIDED triangle mesh (a front riser quad + a top tread quad), NOT a solid box, and that is
// load-bearing: the real building/curb proxies are one-sided meshes, and only a one-sided face reproduces the
// defect. A solid convex box depenetrates a shallow overlap cleanly to a skin-width clearance, so the capped
// advance always re-seats and a box riser mounts fine even on the UNFIXED code; the one-sided face is where the
// depenetration pushback runs past the capped advance and the mount cancels every tick.
public class SingleRiserMountTests
{
    const float Dt = 1f / 60f;
    const float RiserHeight = 0.3f;   // < StepHeight 0.4 so the step-up accepts it; deep enough to stall the cap
    const float StartZ = 1.0f;        // ~1 m in front of the riser face (at Z=0), on the flat floor

    // One-sided step: a +Z-facing riser quad (Y 0..riserHeight at Z=0) and a +Y-facing tread quad (at Y=riserHeight,
    // Z from 0 back to -treadDepth), both wide in X. The riser normal +Z faces the approaching capsule; the tread
    // normal +Y supports it. The tread is DEEP so the capsule stays mounted for the whole drive window (it walks
    // forward onto the tread after seating and must not stroll off the far edge and fall).
    static TriangleMeshShape Step(float riserHeight, float treadDepth = 40f, float halfX = 20f)
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        void Tri(int a, int b, int c) { idx.Add(a); idx.Add(b); idx.Add(c); }

        int b0 = v.Count;                                     // riser face, wound CCW seen from +Z (normal +Z)
        v.Add(new Vector3(-halfX, 0f, 0f));
        v.Add(new Vector3(halfX, 0f, 0f));
        v.Add(new Vector3(halfX, riserHeight, 0f));
        v.Add(new Vector3(-halfX, riserHeight, 0f));
        Tri(b0 + 0, b0 + 2, b0 + 1); Tri(b0 + 0, b0 + 3, b0 + 2);

        b0 = v.Count;                                         // tread top (normal +Y)
        v.Add(new Vector3(-halfX, riserHeight, 0f));
        v.Add(new Vector3(halfX, riserHeight, 0f));
        v.Add(new Vector3(halfX, riserHeight, -treadDepth));
        v.Add(new Vector3(-halfX, riserHeight, -treadDepth));
        Tri(b0 + 0, b0 + 1, b0 + 2); Tri(b0 + 0, b0 + 2, b0 + 3);

        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }

    // Drive a head-on walk (yaw 0 => forward = -Z, into the riser face at Z=0) from StartZ on the flat floor for
    // `ticks`, capturing the per-tick capsule-centre Y. Analytic ground is flat at Y=0 (feet on the ground => centre
    // at half-height), the way the walkable slice / building demos feed terrain (the step is the only physics body).
    static float[] DriveY(in MoveTuning tuning, TriangleMeshShape step, int ticks)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(step, Pose.At(Vector3.Zero));
        world.Step(Dt);   // prime the broad phase (statics don't move, so one step is enough)

        float halfH = tuning.CapsuleHalfHeight;
        var state = new MoveState { Position = new Vector3(0f, halfH, StartZ), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        float GroundHeight(float x, float z) => 0f;
        Func<float, float, Vector3> flatNormal = (x, z) => Vector3.UnitY;   // demos pass a flat ground normal

        var y = new float[ticks];
        for (int i = 0; i < ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, GroundHeight, tuning, flatNormal, world);
            y[i] = state.Position.Y;
        }
        return y;
    }

    static float FlatY(in MoveTuning t) => t.CapsuleHalfHeight;            // feet on the ground at Y=0
    static float MountedY(in MoveTuning t) => RiserHeight + t.CapsuleHalfHeight;

    // First tick the capsule leaves the flat floor (mount engagement); -1 if it never does.
    static int EngageTick(float[] y, float flatY)
    {
        for (int i = 0; i < y.Length; i++) if (y[i] > flatY + 0.01f) return i;
        return -1;
    }

    // First tick the capsule reaches (near) the tread top (seated); -1 if it never does.
    static int SeatedTick(float[] y, float mountedY)
    {
        for (int i = 0; i < y.Length; i++) if (y[i] >= mountedY - 0.02f) return i;
        return -1;
    }

    // Largest single-tick UPWARD Y step over the run (from the flat start height). Falling is not measured.
    static float MaxUpwardStep(float[] y, float startY)
    {
        float prev = startY, maxUp = 0f;
        for (int i = 0; i < y.Length; i++) { maxUp = MathF.Max(maxUp, y[i] - prev); prev = y[i]; }
        return maxUp;
    }

    [Fact]
    public void SlowWalk_SingleRiser_DefaultRadius_Mounts()
    {
        var t = MoveTuning.Default;   // radius 0.4, walk 6, climb 3.5 - the shipped tuning the user hit
        float[] y = DriveY(t, Step(RiserHeight), 180);
        Assert.True(y[^1] > MountedY(t) - 0.05f,
            $"walking into a single {RiserHeight} m riser at the default radius never mounted (vibrated in place): " +
            $"final Y {y[^1]:F3}, expected ~{MountedY(t):F3} (flat {FlatY(t):F3}).");
    }

    [Fact]
    public void SlowWalk_SingleRiser_SmallRadius_Mounts()
    {
        // Demo radius 0.3 (Room3D/RoomDungeon). The investigation's key finding: the stall is NOT about the radius -
        // the SAME one-sided riser buzzes at radius 0.3 too (its depenetration pushback still runs past the capped
        // advance). This is the deep-pushback single-riser case from the building-proxy profile.
        var t = MoveTuning.Default with { CapsuleRadius = 0.3f };
        float[] y = DriveY(t, Step(RiserHeight), 180);
        Assert.True(y[^1] > MountedY(t) - 0.05f,
            $"walking into a single {RiserHeight} m riser at the demo radius 0.3 never mounted (vibrated in place): " +
            $"final Y {y[^1]:F3}, expected ~{MountedY(t):F3} (flat {FlatY(t):F3}).");
    }

    [Fact]
    public void Mount_WithinPacedTimeBudget()
    {
        var t = MoveTuning.Default;
        float[] y = DriveY(t, Step(RiserHeight), 180);
        int engage = EngageTick(y, FlatY(t));
        int seated = SeatedTick(y, MountedY(t));
        Assert.True(engage >= 0 && seated >= engage,
            $"never mounted: engage {engage}, seated {seated}, final Y {y[^1]:F3}");
        // The paced climb rises at MaxStepClimbSpeed, so the riser takes about riser / MaxStepClimbSpeed seconds plus
        // a couple of ticks of seating - NOT the multi-second (hundreds of ticks) recovery a stall-then-escape shows.
        int pacedTicks = (int)MathF.Ceiling(RiserHeight / (t.MaxStepClimbSpeed * Dt));
        int budget = pacedTicks + 4;
        Assert.True(seated - engage <= budget,
            $"mount took {seated - engage} ticks (engage {engage} -> seated {seated}), over the paced budget {budget} " +
            $"(~{RiserHeight} m / {t.MaxStepClimbSpeed} m/s = {pacedTicks} ticks + seating): a stall-then-recover, not a clean paced mount.");
    }

    [Fact]
    public void Climb_VerticalProgress_IsMonotone_WhileMounting()
    {
        var t = MoveTuning.Default;
        float[] y = DriveY(t, Step(RiserHeight), 180);
        int engage = EngageTick(y, FlatY(t));
        Assert.True(engage >= 0, $"never engaged the riser: final Y {y[^1]:F3}");
        // From engagement onward the capsule-centre Y must never DROP. The vibrate signature was a per-tick rise then
        // fall (mount, lose the tread, fall back to flat); a monotone climb is exactly its absence. A tiny epsilon
        // absorbs float noise only. The tread is deep, so the capsule never walks off its far edge in the window.
        for (int i = engage + 1; i < y.Length; i++)
            Assert.True(y[i] >= y[i - 1] - 1e-3f,
                $"vertical progress went BACKWARDS at tick {i}: {y[i - 1]:F4} -> {y[i]:F4} (the rise-fall mount vibrate).");
    }

    [Fact]
    public void Mount_RespectsMaxStepClimbSpeed_EveryTick()
    {
        var t = MoveTuning.Default;
        float[] y = DriveY(t, Step(RiserHeight), 180);
        float maxRise = t.MaxStepClimbSpeed * Dt;
        float maxUp = MaxUpwardStep(y, FlatY(t));
        Assert.True(maxUp <= maxRise + 0.01f,
            $"a tick rose {maxUp:F4} m, exceeding the {maxRise:F4} m/tick climb budget (MaxStepClimbSpeed " +
            $"{t.MaxStepClimbSpeed} m/s): the mount rise is not paced.");
    }

    [Fact]
    public void DisabledPacing_StillSnapsInstantly()
    {
        // MaxStepClimbSpeed <= 0 disables pacing entirely (the documented instant-snap contract): the step-up mounts
        // the whole riser in a single tick, exactly as before any smoothing. The monotone-mount fix lives wholly
        // inside the pacing block (gated on MaxStepClimbSpeed > 0), so disabling it must restore the raw one-tick snap.
        var t = MoveTuning.Default with { MaxStepClimbSpeed = 0f };
        float[] y = DriveY(t, Step(RiserHeight), 60);
        Assert.True(y[^1] > MountedY(t) - 0.05f,
            $"disabled pacing did not mount: final Y {y[^1]:F3}, expected ~{MountedY(t):F3}");
        // Snapped, not paced: one single tick rose by essentially the whole riser (far above a paced budget tick).
        float maxUp = MaxUpwardStep(y, FlatY(t));
        Assert.True(maxUp > 0.5f * RiserHeight,
            $"pacing disabled but the mount was still gradual (max single-tick rise {maxUp:F4} m): the instant snap is broken.");
    }
}
