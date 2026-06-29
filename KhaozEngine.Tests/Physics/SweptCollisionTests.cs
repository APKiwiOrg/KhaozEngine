using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SweptCollisionTests
{
    private static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f, MaxSlopeRadians: 0.9f);
    private static float Flat(float x, float z) => 0f;

    // One-sided thin quad wall in the XY plane at z=2 (front face normal -Z, toward the approaching capsule),
    // spanning x[-10,10] (wide enough to block for a full sliding test), y[0,3]. A single quad => two triangles,
    // ~0.0 m thick: the classic tunnel trap.
    private static TriangleMeshShape ThinWallAtZ2()
    {
        var v = new[]
        {
            new Vector3(-10f, 0f, 2f), new Vector3(10f, 0f, 2f),
            new Vector3(10f, 3f, 2f), new Vector3(-10f, 3f, 2f),
        };
        // Wound so the front face normal points -Z (toward the capsule coming from z<2).
        var idx = new[] { 0, 2, 1, 0, 3, 2 };
        return new TriangleMeshShape(v, idx);
    }

    [Fact]
    public void FastMove_DoesNotTunnelThroughThinOneSidedWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Drive straight toward +Z (Move.Y=-1 at yaw=0 => +Z). A LARGE dt (0.1 s) at run speed makes one tick's
        // displacement ~0.6 m, well over the 0.4 m capsule radius - the regime where the old teleport-then-
        // depenetrate resolver tunnels through the one-sided quad (low-frame-rate clients hit exactly this).
        const float BigDt = 0.1f;
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
            state = CharacterMovement.Step(state, run, BigDt, Flat, Tuning, groundNormal: null, world: world);

        // The capsule centre must stay on the near side of the wall (z < 2 - radius + skin), never past it.
        Assert.True(state.Position.Z < 1.65f, $"tunneled through the thin wall, z={state.Position.Z}");
    }

    [Fact]
    public void Diagonal_SlidesAlongWall_NoPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Move diagonally into the wall (toward +Z and +X): expect blocked in Z, sliding in +X.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        Assert.True(state.Position.Z < 1.65f, $"penetrated/over-advanced into wall, z={state.Position.Z}");
        Assert.True(state.Position.X > 1.0f, $"did not slide along the wall, x={state.Position.X}");
    }

    [Fact]
    public void InnerCorner_StopsWithoutPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));            // wall at z=2 (faces -Z)
        // Side wall at x=2 facing -X: quad in the ZY plane.
        var sv = new[]
        {
            new Vector3(2f, 0f, -3f), new Vector3(2f, 0f, 3f),
            new Vector3(2f, 3f, 3f), new Vector3(2f, 3f, -3f),
        };
        world.AddStatic(new TriangleMeshShape(sv, new[] { 0, 1, 2, 0, 2, 3 }), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Wedged into the corner: stopped short of both faces (centre within radius+skin of each).
        Assert.True(state.Position.Z < 1.65f && state.Position.X < 1.65f,
            $"corner not respected: pos={state.Position}");
        // And stable (no NaN / fling).
        Assert.True(float.IsFinite(state.Position.X) && float.IsFinite(state.Position.Z));
    }

    [Fact]
    public void FastPath_IsDeterministic_AcrossTwoWorlds()
    {
        static MoveState RunOnce()
        {
            IPhysicsWorld world = new BepuPhysicsWorld();
            world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
            world.Step(1f / 60f);
            var s = new MoveState { Position = new Vector3(0.13f, 0.9f, 0f), Grounded = true };
            var cmd = new MoveCommand(new Vector2(0.3f, -1f), run: true, cameraYaw: 0.2f, jump: false);
            for (int i = 0; i < 200; i++)
                s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            world.Dispose();
            return s;
        }

        MoveState a = RunOnce(), b = RunOnce();
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.X), BitConverter.SingleToInt32Bits(b.Position.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Y), BitConverter.SingleToInt32Bits(b.Position.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Z), BitConverter.SingleToInt32Bits(b.Position.Z));
    }
}
