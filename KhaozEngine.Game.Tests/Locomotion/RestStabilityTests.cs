using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// Rest-stability regressions for the character controller: a grounded capsule at rest must not creep, a capsule
/// released mid-stair-climb must not slide back or down, and a first-riser mount must not flicker airborne or lurch.
/// These pin the three defects the vertical-rest depenetration (walkable contacts resolve vertically when there is no
/// horizontal command) and the zero-input monotone hold were added to kill.
///
/// Backdrop (why these were needed): the swept depenetration passes used to correct a grounded capsule along the FULL
/// contact MTV including its horizontal component, even at rest on a walkable surface, so a capsule standing on a
/// tilted PHYSICS prop crept down-slope by ResolveSlop*sin(slope) every tick (analytic terrain is immune - it is not
/// in the physics world). The same full-MTV push shoved a capsule released mid-climb backward off the riser it was
/// mounting. The fixes resolve a resting (no-command) grounded capsule's WALKABLE-contact depenetration vertically
/// only, and hold XZ on a zero-command tick while grounded and elevated on a step. A steep (wall/riser) contact still
/// takes the full MTV, so walking INTO a wall is unchanged (pinned by SweptCollisionTests and below).
/// </summary>
public class RestStabilityTests
{
    const float Dt = 1f / 60f;
    static float Flat(float x, float z) => 0f;
    static readonly Func<float, float, Vector3> FlatNormal = (x, z) => Vector3.UnitY;

    // ---- A. Rest on a tilted walkable prop: no down-slope creep ------------------------------------------------

    // A wide thin box tilted about X by theta, centred well above flat terrain so its top is a walkable prop surface.
    static Pose TiltedBox(float thetaDeg, out BoxShape box)
    {
        box = new BoxShape(new Vector3(6f, 0.5f, 6f));
        return new Pose(new Vector3(0f, 3f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, thetaDeg * MathF.PI / 180f));
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(20f)]
    [InlineData(30f)]
    [InlineData(38f)]   // still under the 45 deg slope gate: a walkable surface a capsule may stand on
    public void IdleOnWalkablePropSlope_DoesNotDrift(float slopeDeg)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        Pose pose = TiltedBox(slopeDeg, out BoxShape box);
        world.AddStatic(box, pose);
        world.Step(Dt);

        var t = MoveTuning.Default;
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);

        // Drop onto the tilted top and settle with zero input.
        var state = new MoveState { Position = new Vector3(0f, 6f, 0f), Grounded = false };
        for (int i = 0; i < 120; i++) state = CharacterMovement.Step(state, idle, Dt, Flat, t, FlatNormal, world);
        Assert.True(state.Grounded, $"capsule failed to settle on the {slopeDeg} deg walkable box");

        Vector3 settled = state.Position;
        float maxPlanarDrift = 0f;
        for (int i = 0; i < 300; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Flat, t, FlatNormal, world);
            float dx = state.Position.X - settled.X, dz = state.Position.Z - settled.Z;
            maxPlanarDrift = MathF.Max(maxPlanarDrift, MathF.Sqrt(dx * dx + dz * dz));
        }

        // Pre-fix this crept down-slope at exactly ResolveSlop*sin(slope) per tick (~0.5 m at 10 deg, ~1.9 m at 38 deg
        // over 300 ticks). A resting capsule on a walkable surface must be static, matching analytic terrain.
        Assert.True(maxPlanarDrift < 0.001f,
            $"idle capsule crept {maxPlanarDrift:F4} m on a {slopeDeg} deg walkable prop slope (must be < 1 mm).");
    }

    // ---- B. Release input mid-stair-climb: no slide back or down ------------------------------------------------

    // Solid convex box columns like the consumer TestStaircase: riser 0.30 (< StepHeight 0.4), tread 0.40, buried 1.5,
    // climbing toward -Z (first riser face at Z=0). This is the geometry the shipped stair mount is tuned against. Kept
    // WIDE in X (half-extent 8) so an angled approach cannot drift off the side edge and confound the mount question.
    static void AddSolidStaircase(IPhysicsWorld world, float rise = 0.30f, float tread = 0.40f, int steps = 33)
    {
        for (int n = 0; n < steps; n++)
        {
            float treadTop = (n + 1) * rise;
            float halfH = (treadTop + 1.5f) * 0.5f;
            world.AddStatic(new BoxShape(new Vector3(8f, halfH, tread * 0.5f)),
                Pose.At(new Vector3(0f, treadTop - halfH, -(n * tread + tread * 0.5f))));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseOnStairTread_DoesNotSlideBackOrDown(bool run)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddSolidStaircase(world);
        world.Step(Dt);

        var t = MoveTuning.Default;
        float halfH = t.CapsuleHalfHeight;
        var climb = new MoveCommand(new Vector2(0f, 1f), run: run, cameraYaw: 0f, jump: false);   // forward = -Z, up the stairs
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);

        // Climb until well up onto the stairs (a few risers), then release.
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        for (int i = 0; i < 120 && state.Position.Y < halfH + 0.9f; i++)
            state = CharacterMovement.Step(state, climb, Dt, Flat, t, null, world);
        Assert.True(state.Position.Y > halfH + 0.6f, $"precondition: capsule should be mid-climb, y={state.Position.Y:F2}");

        Vector3 atRelease = state.Position;

        // Tick 0 of the release: planar speed must be near zero (below the facing/turn threshold). Pre-fix the tick-0
        // settle depenetration shoved the capsule backward off the riser (2 m/s at walk, a 0.4 m single-tick snap at run).
        var afterFirst = CharacterMovement.Step(state, idle, Dt, Flat, t, null, world);
        float tick0Planar = Planar(afterFirst.Position, atRelease);
        Assert.True(tick0Planar / Dt < 0.05f,
            $"released mid-climb ({(run ? "run" : "walk")}): tick-0 planar speed {tick0Planar / Dt:F3} m/s (must be < 0.05).");

        // Over the whole release settle: no net planar drift and the height never drops a riser below where it was.
        state = afterFirst;
        float minY = state.Position.Y;
        for (int i = 0; i < 90; i++)
        {
            state = CharacterMovement.Step(state, idle, Dt, Flat, t, null, world);
            minY = MathF.Min(minY, state.Position.Y);
        }
        Assert.True(Planar(state.Position, atRelease) < 0.1f,
            $"released mid-climb ({(run ? "run" : "walk")}): net planar drift {Planar(state.Position, atRelease):F3} m (must be < 0.1).");
        Assert.True(minY > atRelease.Y - 0.30f + 0.01f,
            $"released mid-climb ({(run ? "run" : "walk")}): dropped to y={minY:F3}, a full riser below release y={atRelease.Y:F3}.");
    }

    // ---- C. First-riser mount from terrain: no airborne flicker, no big Y jump, no stall -----------------------

    // Root cause C's SUBSTANTIVE symptoms were an airborne grounded-flicker (the avatar snap-to-physics branch fires
    // on !Grounded and pops the model + camera) and 0.5 m single-tick Y jumps on angled approaches. Across phase +
    // approach angle + walk/run, the mount must (1) stay grounded every tick (the residual sub-riser physics-Y dip is
    // then absorbed by the CharacterAvatar RenderY smoothing per the documented render-dip convention), (2) never jump
    // more than a third of a metre in one tick, and (3) actually reach the top (no stall). This is the outcome-level
    // pin; StairRunTangentPacingTests / StairAscentFeelTests pin the finer run-smoothness + render monotonicity.
    [Theory]
    [InlineData(false, 0f)]
    [InlineData(false, 15f)]
    [InlineData(false, -12f)]
    [InlineData(true, 0f)]
    [InlineData(true, 8f)]
    [InlineData(true, 15f)]
    public void FirstRiserMount_NoFlickerNoLurch_AcrossPhase(bool run, float approachAngleDeg)
    {
        var t = MoveTuning.Default;
        float halfH = t.CapsuleHalfHeight;
        float a = approachAngleDeg * MathF.PI / 180f;
        var moveInput = new Vector2(MathF.Sin(a), MathF.Cos(a));   // mostly forward (-Z), a lateral component off-square

        // Sweep the approach phase (where the footprint first meets the riser edge): the yo-yo / flicker was
        // phase-dependent, so several approach distances are driven, each landing a tick on the riser at a different
        // sub-tick phase. All start CLEAR of the first riser (footprint radius 0.4, riser at Z=0, so startZ > 0.4), i.e.
        // a genuine approach from the flat floor - not a capsule spawned already embedded in the step.
        foreach (float startZ in new[] { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.15f, 1.3f })
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            AddSolidStaircase(world);
            world.Step(Dt);

            var state = new MoveState { Position = new Vector3(0f, halfH, startZ), Grounded = true };
            var cmd = new MoveCommand(moveInput, run: run, cameraYaw: 0f, jump: false);

            float prevY = state.Position.Y;
            bool everAirborne = false, mounted = false;
            float maxJump = 0f;
            for (int i = 0; i < 60; i++)
            {
                state = CharacterMovement.Step(state, cmd, Dt, Flat, t, null, world);
                maxJump = MathF.Max(maxJump, MathF.Abs(state.Position.Y - prevY));
                prevY = state.Position.Y;
                if (!state.Grounded) everAirborne = true;
                if (state.Position.Y > halfH + 0.6f) mounted = true;   // climbed at least two risers => genuinely mounting
            }

            string tag = $"{(run ? "run" : "walk")} ang={approachAngleDeg} startZ={startZ:F2}";
            Assert.False(everAirborne,
                $"first-riser mount flickered AIRBORNE ({tag}): the avatar would snap-pop. The mount must stay grounded.");
            Assert.True(maxJump < 0.35f,
                $"first-riser mount lurched {maxJump:F3} m in one tick ({tag}): the ~0.5 m single-tick Y jump is back.");
            Assert.True(mounted,
                $"first-riser mount never climbed onto the stairs ({tag}): a stall, final y={state.Position.Y:F3}.");
        }
    }

    // ---- Fix-1 boundary: walking INTO a wall still depenetrates HORIZONTALLY (non-walkable normal, full MTV) ------

    // The vertical-rest depenetration ONLY resolves WALKABLE contacts vertically, and ONLY when there is no horizontal
    // command. A grounded capsule walking straight into a vertical wall is a STEEP (non-walkable) contact WITH a
    // command, so it must still take the full horizontal MTV and be blocked - never creep through. (SweptCollisionTests
    // pins this broadly; this is the explicit fix-boundary pin.)
    [Fact]
    public void GroundedWalkIntoWall_StillBlockedHorizontally()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // A tall thin wall spanning X, its near face at Z = 0 (front at Z=0, body at Z<0).
        world.AddStatic(new BoxShape(new Vector3(10f, 5f, 1f)), Pose.At(new Vector3(0f, 5f, -1f)));
        world.Step(Dt);

        var t = MoveTuning.Default;
        float halfH = t.CapsuleHalfHeight;
        var state = new MoveState { Position = new Vector3(0f, halfH, 2.0f), Grounded = true };
        var intoWall = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f, jump: false);   // forward = -Z, into the wall

        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, intoWall, Dt, Flat, t, FlatNormal, world);

        // Blocked at the wall face (near face at Z=0, minus the capsule radius + skin): never pushed through into Z < 0.
        Assert.True(state.Position.Z > 0.35f,
            $"capsule walking INTO the wall was not depenetrated horizontally (z={state.Position.Z:F3}); it tunneled/crept through.");
        Assert.True(state.Grounded, "capsule walking into a wall on flat ground must stay grounded");
    }

    // ---- C (angled, real proxy): angled approach at a building step must not FLICKER airborne -------------------

    // Root cause C's grounded flicker at its worst: a capsule pressed at an ANGLE against a real one-sided building
    // step (the baked inn-door proxy) had the step-up lift it a hair above terrain, its feet-down ray fan then miss the
    // narrow step, and `grounded` flip false for many ticks - 8-14 airborne ticks out of 120 in the sweep - while the
    // body was plainly still embedded in the step. Every such tick is an avatar snap-to-physics pop of the model +
    // camera. The step-contact grounded-hysteresis (a static overlap => a step contact remains => stay grounded) kills
    // it: the capsule stays grounded through the angled press. Straight-on (the InnDoorStepMountTests pin) mounts as
    // before; this pins the ANGLED grounded-stability the bare ray fan could not hold.
    const float InnScale = 1.5f;

    static PhysicsShape InnProxy()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "inn_proxy.coll");
        return PhysicsShapeScale.Uniform(PropCollisionFormat.Read(path), InnScale);
    }

    [Theory]
    [InlineData(-12f, false)]
    [InlineData(-12f, true)]
    [InlineData(12f, false)]
    [InlineData(20f, true)]
    public void AngledApproachAtBuildingStep_DoesNotFlickerAirborne(float approachAngleDeg, bool run)
    {
        // Inn tuning matches InnDoorStepMountTests (a 40 deg slope gate for the baked proxy).
        var t = MoveTuning.Default with { MaxSlopeRadians = MathF.PI * 40f / 180f };
        static float Ground(float x, float z) => 0f;

        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(InnProxy(), Pose.At(Vector3.Zero));
        world.Step(Dt);

        // Settle on the flat approach in front of the door (same spawn as InnDoorStepMountTests).
        var s = new MoveState { Position = new Vector3(0f, 22f, 5.4f), Grounded = false };
        for (int i = 0; i < 420; i++)
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, false, 0f, false), Dt, Ground, t, null, world);
        Assert.True(s.Grounded, "precondition: capsule should settle grounded on the flat approach");

        float a = approachAngleDeg * MathF.PI / 180f;
        var cmd = new MoveCommand(new Vector2(MathF.Sin(a), MathF.Cos(a)), run, 0f, false);   // angled walk/run at the step (-Z)
        int airborneTicks = 0;
        for (int i = 0; i < 120; i++)
        {
            s = CharacterMovement.Step(s, cmd, Dt, Ground, t, null, world);
            if (!s.Grounded) airborneTicks++;
        }

        // Pre-hysteresis this flickered 8-14 airborne ticks (the avatar would snap-pop each one). A capsule pressed
        // against a step on flat ground is grounded; allow a tiny transition margin only.
        Assert.True(airborneTicks <= 2,
            $"angled approach ({(run ? "run" : "walk")} {approachAngleDeg} deg) flickered airborne {airborneTicks} " +
            "ticks against the building step (the grounded-stick lost the embedded step contact).");
    }

    static float Planar(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
