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

        // Must rest on the dome surface, not clip through it.
        Assert.True(state.Position.Y > 0.2f, $"capsule should rest on the dome, was y={state.Position.Y}");
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

        // Wall face is at z = 2 - 0.25 = 1.75; capsule radius 0.4 so stop < 1.75 - 0.4 ~ 1.35.
        Assert.True(state.Position.Z < 1.7f, $"should be blocked before the wall, was z={state.Position.Z}");
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
