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
    // toward +Z and hits the angled face. The correct collide-and-slide order (consume-then-project)
    // produces meaningful lateral travel; the old order (project-then-subtract) introduced a spurious
    // backward component that reduced net slide by ~0.02 m per contact and under-slid the residue.
    // Thresholds are measured from the fixed implementation (x=-2.0657, z=2.4221 after 2s).
    // Under the old ordering the settled position was x=-2.0785, z=2.4000, which fails both checks.
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
        // With correct ordering: settled at x~-2.066, z~2.422.
        // With old ordering:     settled at x~-2.079, z~2.400. Both thresholds fail under the bug.
        Assert.True(state.Position.Z > 2.41f,
            $"under-slide in +Z: expected > 2.41 but was z={state.Position.Z:F4} (old ordering produces ~2.400)");
        Assert.True(state.Position.X > -2.07f,
            $"over-slide in -X: expected > -2.07 but was x={state.Position.X:F4} (old ordering produces ~-2.079)");
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
}
