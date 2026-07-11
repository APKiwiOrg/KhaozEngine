using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class ControllerOnPhysicsTests
{
    // CapsuleHalfHeight 0.9 => 1.8 m total; CapsuleRadius 0.4; MaxSlopeRadians ~51 degrees.
    private static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: 0.9f);

    private static float Flat(float x, float z) => 0f;

    // A dome: sphere radius 2, centre at (0,-1,0) => top surface at y=1.
    // Place the capsule off-centre on the flank and let it settle under gravity.
    [Fact]
    public void Capsule_RestsOnDomeFlank_WithoutPenetrating()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new SphereShape(2f), Pose.At(new Vector3(0f, -1f, 0f)));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(1.0f, 2.0f, 0f), Grounded = false };
        var cmd = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 30; i++)
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Must rest ON the dome surface without clipping through it.
        // True no-penetration check: distance from sphere centre (0,-1,0) to the capsule vertical segment
        // must be >= sphere_radius + capsule_radius - skin.
        // Capsule: CapsuleFor(Tuning) => radius 0.4, cylinderLength = max(0.01, 2*0.9 - 2*0.4) = 1.0.
        // The vertical segment spans [centreY - 0.5, centreY + 0.5] at (x, z).
        // Closest point on that segment to sphere centre (0,-1,0):
        var sphereCentre = new Vector3(0f, -1f, 0f);
        float cx = state.Position.X, cz = state.Position.Z, cy = state.Position.Y;
        float capsuleCylinderHalfLen = 0.5f; // half the cylindrical segment length (1.0 / 2)
        float segMinY = cy - capsuleCylinderHalfLen;
        float segMaxY = cy + capsuleCylinderHalfLen;
        float closestY = MathF.Max(segMinY, MathF.Min(sphereCentre.Y, segMaxY));
        var closestPt = new Vector3(cx, closestY, cz);
        float distCentreToSegment = Vector3.Distance(sphereCentre, closestPt);
        const float SphereRadius = 2f;
        const float CapsuleRadius = 0.4f;
        const float PenetrationSkin = 0.05f;
        Assert.True(
            distCentreToSegment >= SphereRadius + CapsuleRadius - PenetrationSkin,
            $"capsule clips into dome: dist={distCentreToSegment:F4} < {SphereRadius + CapsuleRadius - PenetrationSkin:F4}, pos={state.Position}");
        Assert.True(state.Grounded, $"should be grounded on the dome, grounded={state.Grounded}");
    }

    // A wall at z=2, capsule walks toward +Z (which is -Y move at yaw=0 in the camera basis
    // where forward = -Z; however to walk toward +Z we need forward +Z => use yaw=pi so forward=+Z).
    // Actually the camera basis: forward = (-sin(yaw), 0, -cos(yaw)).
    // At yaw=0: forward = (0,0,-1). Move.Y=1 => toward -Z (away from wall at z=2).
    // To walk toward +Z (toward the wall): forward must be +Z, so -cos(yaw)=1 => cos(yaw)=-1 => yaw=pi.
    // Alternatively at yaw=0: Move.Y=-1 walks in forward=-Z... wait: Move.Y=1 * forward(0,0,-1) => dx=0,dz=-1 * speed * dt.
    // To move toward +Z: we want dz positive. That means Move.Y * forward.Z > 0.
    // forward.Z = -cos(yaw). For yaw=0: forward.Z = -1. So Move.Y=-1 gives -1 * -1 = +1 contribution: toward +Z. Yes.
    [Fact]
    public void Capsule_CannotWalkThroughAWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        // Move.Y = -1 at CameraYaw=0 => forward = (0,0,-1), move = -1 * forward = (0,0,+1) => toward +Z (wall).
        var toward = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, toward, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Wall face is at z = 2 - 0.125 = 1.875; capsule radius 0.4 so stopped centre z ~ 1.875 - 0.4 = 1.475.
        // Upper bound tightened to catch tunnel-through: allow a small skin margin above the ideal stop.
        Assert.True(state.Position.Z < 1.55f, $"should be blocked before the wall, was z={state.Position.Z}");
    }

    // Oblique-slide regression: wall rotated 30 degrees around Y (yaw Pose), capsule walks straight
    // toward +Z and hits the angled face. The swept collide-and-slide resolver correctly blocks the
    // capsule at the wall surface and projects the remaining motion onto the contact plane, producing
    // meaningful lateral travel in -X and forward advance in +Z.
    // Thresholds are measured from the swept resolver (x=-1.6496, z=2.1919 after 2s). The swept
    // resolver settles differently from the old teleport-then-depenetrate resolver because the capsule
    // never enters the wall face; instead the slide plane is the wall normal's contact plane from
    // the first hit, which is geometrically different from pushing out of a penetrating position.
    [Fact]
    public void Capsule_SlidesAlongObliqueWall_CorrectlyAdvances()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Box rotated 30 degrees (pi/6) around Y so its face normal has both X and Z components.
        // Wide enough (10 m) to block the character for the full 2-second test.
        var rot30 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f);
        world.AddStatic(new BoxShape(new Vector3(10f, 2f, 0.25f)), new Pose(new Vector3(0f, 1f, 2f), rot30));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        // Walk straight toward +Z (perpendicular to the unrotated wall direction, oblique to the
        // rotated face normal). Move.Y=-1 at yaw=0 => direction (0,0,+1).
        var toward = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, toward, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Character must slide meaningfully in -X (along the oblique wall tangent) and advance in +Z.
        // Swept resolver settles at x~-1.650, z~2.192 (measured from the implementation).
        Assert.True(state.Position.Z > 2.18f,
            $"under-slide in +Z: expected > 2.18 but was z={state.Position.Z:F4}");
        Assert.True(state.Position.X > -1.66f,
            $"over-slide in -X: expected > -1.66 but was x={state.Position.X:F4}");
    }

    // Two wall panels with a gap (doorway) at x=0. Capsule at origin walks through.
    [Fact]
    public void Capsule_WalksThroughADoorwayGap()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Left panel: centred at x=-2, half-width 1.5 => occupies x in [-3.5, -0.5].
        world.AddStatic(new BoxShape(new Vector3(1.5f, 2f, 0.25f)), Pose.At(new Vector3(-2.0f, 1f, 2f)));
        // Right panel: centred at x=+2, half-width 1.5 => occupies x in [0.5, 3.5].
        world.AddStatic(new BoxShape(new Vector3(1.5f, 2f, 0.25f)), Pose.At(new Vector3(2.0f, 1f, 2f)));
        world.Step(1f / 60f);

        // Gap at centre: from x=-0.5 to x=0.5 (1 m wide); capsule radius 0.4 => just fits.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var toward = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 180; i++)
            state = CharacterMovement.Step(state, toward, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Should be well past z=2 after 3 seconds walking at 3 m/s (up to ~9 m).
        Assert.True(state.Position.Z > 3f, $"should pass through the doorway gap, was z={state.Position.Z}");
    }

    // When world=null the new overload falls back to terrain-only (no collision, no support probe).
    [Fact]
    public void NullWorld_IsTerrainOnly_Unchanged()
    {
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        var moved = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: null);
        // Moved freely toward +Z (no wall to block).
        Assert.True(moved.Position.Z > state.Position.Z, $"should move freely, z={moved.Position.Z}");
        // Grounded on flat terrain: y = groundHeight(x,z) + halfHeight = 0 + 0.9 = 0.9.
        Assert.Equal(0.9f, moved.Position.Y, 3);
    }

    // Regression for the historically bug-prone "domed prop is mountable by jumping onto it from the side"
    // path. The dome: SphereShape(2) centred at (0,-1,0), top surface at y=1. Capsule starts grounded on
    // flat terrain at (0, 0.9, 3.5), walks toward -Z (into the dome) while jumping from tick 0. Jump apex
    // ~2.18 m clears the dome top (capsule rests at ~1.9 m on the dome). On descent the 3D depenetration push
    // is upward-dominant on the dome top, so the capsule settles on the dome surface and reads grounded.
    // Asserts: grounded on the dome (Y > terrain rest height + 0.1 m margin) and no penetration.
    [Fact]
    public void Capsule_MountsDomeFromSide_ByJumping()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new SphereShape(2f), Pose.At(new Vector3(0f, -1f, 0f)));
        world.Step(1f / 60f);

        // Start grounded outside the dome footprint, slightly left of centre so we approach along -Z.
        // At yaw=0: forward = (0,0,-1). Move.Y=1 => direction (0,0,-1) = toward -Z => toward dome.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 3.5f), Grounded = true };
        // Jump from the very first tick and keep walking toward the dome.
        var cmdJump   = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: true);
        var cmdWalk   = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);

        const float CapsuleHalfHeight = 0.9f;
        var sphereCentre = new Vector3(0f, -1f, 0f);
        const float SphereRadius = 2f;
        const float CapsuleRadius = 0.4f;
        const float PenetrationSkin = 0.05f;
        float minSeparation = SphereRadius + CapsuleRadius - PenetrationSkin;

        // The capsule jumps onto the dome, walks up over the top, and rides down the far side: it is grounded
        // ELEVATED on the dome surface for a stretch of the run (mounted), then returns to flat terrain past it.
        // Track that it was grounded on the dome at all (the mount) and that it never penetrated the sphere.
        bool everMountedGrounded = false;
        for (int i = 0; i < 180; i++)
        {
            // Hold jump input for 6 ticks (~0.1 s) to ensure it registers, then release.
            var cmd = i < 6 ? cmdJump : cmdWalk;
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

            // Mounted = grounded with the centre clearly above terrain rest (so it is standing on the dome,
            // not back on flat ground). A 0.1 m margin above terrain rest proves it settled on the dome surface.
            if (state.Grounded && state.Position.Y > CapsuleHalfHeight + 0.1f) everMountedGrounded = true;

            // No-penetration EVERY tick: the same segment-distance check as the dome-rest test.
            float cx = state.Position.X, cz = state.Position.Z, cy = state.Position.Y;
            float segMinY = cy - 0.5f, segMaxY = cy + 0.5f;
            float closestY = MathF.Max(segMinY, MathF.Min(sphereCentre.Y, segMaxY));
            float dist = Vector3.Distance(sphereCentre, new Vector3(cx, closestY, cz));
            Assert.True(dist >= minSeparation,
                $"tick {i}: capsule clips into dome: dist={dist:F4} < {minSeparation:F4}, pos={state.Position}");
        }

        Assert.True(everMountedGrounded,
            "capsule must mount the dome (be grounded elevated above terrain at some point) by jumping onto it");
    }

    // Ledge-release safety for the stair-climb grounded-stick (CharacterMovement step 4a). That stick holds the
    // capsule grounded when it was grounded, sits above the terrain floor, and a walkable surface is still within
    // the feet-down ray fan - so a paced stair climb never reads airborne between steps. This pins the OTHER side:
    // it must NOT keep a capsule stuck/floating when it genuinely walks off a ledge (no support under the feet).
    // No existing with-world test covers this: the fall tests all START airborne. Here a GROUNDED capsule stands
    // on a raised static box (top at y=3) over open terrain far below (analytic ground at y=0), walks off the
    // -Z edge, and must go airborne and FALL below the box top - not hang at the edge or float at box-top height.
    [Fact]
    public void GroundedCapsule_WalksOffLedge_ReleasesAndFalls()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Raised platform: half-extents (2, 1.5, 2) centred at (0, 1.5, 0) => top surface at y = 3.0, spanning
        // x,z in [-2, 2]. The only static in the world; the terrain below is analytic (Flat => y=0), so once the
        // capsule clears the box footprint there is genuinely nothing under its feet in the physics world.
        const float BoxTop = 3.0f;
        world.AddStatic(new BoxShape(new Vector3(2f, 1.5f, 2f)), Pose.At(new Vector3(0f, 1.5f, 0f)));
        world.Step(1f / 60f);

        const float HalfH = 0.9f;
        // Start GROUNDED, resting on the box top (centre = box top + half-height), near the centre of the platform.
        var state = new MoveState { Position = new Vector3(0f, BoxTop + HalfH, 0f), Grounded = true };
        // At yaw=0: forward = (0,0,-1). Move.Y=1 => walk toward -Z, straight off the -Z edge at z=-2.
        var offTheEdge = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);

        // Confirm the precondition really holds: one tick in, the capsule is still grounded on the box (this is a
        // grounded-off-a-ledge test, not an already-airborne one).
        state = CharacterMovement.Step(state, offTheEdge, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Grounded, "precondition: the capsule must start grounded on the platform");

        bool releasedAndFell = false;   // at some tick past the edge: airborne AND below the box top (a real fall)
        float minZ = state.Position.Z;
        for (int i = 0; i < 200; i++)
        {
            state = CharacterMovement.Step(state, offTheEdge, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            minZ = MathF.Min(minZ, state.Position.Z);
            // The stick must let go once there is no support under the feet: airborne while dropping below the box
            // top proves it neither hung grounded at the edge nor floated at box-top height.
            if (!state.Grounded && state.Position.Y < BoxTop - 0.05f) releasedAndFell = true;
        }

        // Sanity: it actually walked off the platform footprint (radius 0.4 fully clears the z=-2 edge past ~-2.4),
        // so the assertion is not vacuous.
        Assert.True(minZ < -2.4f, $"capsule did not walk off the platform, min z={minZ:F3}");
        // The core release safety: it went airborne and fell below the box top instead of sticking/floating.
        Assert.True(releasedAndFell,
            $"grounded-stick over-held: capsule never released off the ledge (final grounded={state.Grounded}, y={state.Position.Y:F3})");
        // And it fell all the way to the terrain far below (rest y = 0 + half-height), landing grounded again.
        Assert.True(state.Grounded && MathF.Abs(state.Position.Y - HalfH) < 0.2f,
            $"capsule did not settle on the terrain below the ledge: grounded={state.Grounded}, y={state.Position.Y:F3} (expected ~{HalfH:F3})");
    }

    // Regression for the no-tunnel base-blocking invariant: walking horizontally into the dome at ground
    // level must never penetrate the sphere, whether the capsule stops, slides laterally, or rides up.
    // Dome: SphereShape(2) centred at (0,-1,0). Capsule starts at (0, 0.9, 3.0) and walks toward -Z.
    [Fact]
    public void Capsule_BlockedAtDomeBase_DoesNotPenetrate()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new SphereShape(2f), Pose.At(new Vector3(0f, -1f, 0f)));
        world.Step(1f / 60f);

        // At yaw=0: Move.Y=1 => direction (0,0,-1) = toward -Z => toward dome centre.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 3.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);

        var sphereCentre = new Vector3(0f, -1f, 0f);
        const float SphereRadius = 2f;
        const float CapsuleRadius = 0.4f;
        const float PenetrationSkin = 0.05f;
        float capsuleCylinderHalfLen = 0.5f;
        float minSeparation = SphereRadius + CapsuleRadius - PenetrationSkin;

        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

            // Check no-penetration EVERY tick.
            float cx = state.Position.X, cz = state.Position.Z, cy = state.Position.Y;
            float segMinY = cy - capsuleCylinderHalfLen;
            float segMaxY = cy + capsuleCylinderHalfLen;
            float closestY = MathF.Max(segMinY, MathF.Min(sphereCentre.Y, segMaxY));
            var closestPt = new Vector3(cx, closestY, cz);
            float dist = Vector3.Distance(sphereCentre, closestPt);
            Assert.True(dist >= minSeparation,
                $"tick {i}: capsule clips into dome: dist={dist:F4} < {minSeparation:F4}, pos={state.Position}");
        }

        // Bonus: capsule must not have passed through the dome entirely (Z should remain > -2.5 or similar).
        // If it tunnelled, Z would be near 0 or past; blocked or slid it stays > 1.0.
        Assert.True(state.Position.Z > 1.0f,
            $"capsule tunnelled through dome, ended at z={state.Position.Z:F4}");
    }
}
