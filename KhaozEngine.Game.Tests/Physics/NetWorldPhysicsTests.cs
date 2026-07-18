using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class NetWorldPhysicsTests
{
    private static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: 0.9f);

    private static float Flat(float x, float z) => 0f;

    // A wall at z=2; simulator steps a player toward it; must be blocked.
    [Fact]
    public void Simulator_WithPhysicsWorld_BlocksPlayerAtWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        world.Step(1f / 30f);

        var sim = new PlayerMoveSimulator(Flat, Tuning, physics: world);
        var state = new PlayerMoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        // Move.Y=-1 at yaw=0 => toward +Z (wall at z=2).
        var toward = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        for (int i = 0; i < 90; i++)
            state = sim.Step(state, toward, 1f / 30f);

        // Should be blocked well before z=2 (wall face at z=1.875, capsule radius 0.4 => stop near z~1.475).
        Assert.True(state.Position.Z < 1.6f,
            $"simulator should be blocked by wall, was z={state.Position.Z}");
    }

    // A box platform whose top is at a known height; drop the player from above and verify it settles there.
    // This exercises the downward support-probe path (the IPhysicsWorld replacement for WorldSurfaces).
    [Fact]
    public void Simulator_WithPhysicsWorld_StandsPlayerOnBox()
    {
        // BoxShape(halfExtents): half-extent Y = 1, centred at y=1 => top of box at y=2.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 0f)));
        world.Step(1f / 30f);

        var sim = new PlayerMoveSimulator(Flat, Tuning, physics: world);
        // Start above the box top (y=2), directly over it (x=0, z=0).
        var state = new PlayerMoveState { Position = new Vector3(0f, 4f, 0f), Grounded = false };
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);

        for (int i = 0; i < 120; i++)
            state = sim.Step(state, idle, 1f / 30f);

        // Capsule centre should settle at boxTop + CapsuleHalfHeight = 2 + 0.9 = 2.9.
        const float boxTop = 2f;
        const float expected = boxTop + 0.9f;    // Tuning.CapsuleHalfHeight
        Assert.True(state.Grounded,
            $"player should be grounded on the box, was Grounded={state.Grounded} at y={state.Position.Y}");
        Assert.Equal(expected, state.Position.Y, 1);
    }

    // Two SEPARATE BepuPhysicsWorld instances with identical geometry must produce identical trajectories.
    // This proves cross-instance determinism (the real deployment has independent server/client worlds).
    [Fact]
    public void TwoSimulators_OverSeparateWorlds_ProduceIdenticalTrajectories()
    {
        using IPhysicsWorld serverWorld = new BepuPhysicsWorld();
        serverWorld.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        serverWorld.Step(1f / 30f);

        using IPhysicsWorld clientWorld = new BepuPhysicsWorld();
        clientWorld.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        clientWorld.Step(1f / 30f);

        var serverSim = new PlayerMoveSimulator(Flat, Tuning, physics: serverWorld);
        var clientSim = new PlayerMoveSimulator(Flat, Tuning, physics: clientWorld);

        var initState = new PlayerMoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        PlayerMoveState serverState = initState;
        PlayerMoveState clientState = initState;

        for (int i = 0; i < 60; i++)
        {
            serverState = serverSim.Step(serverState, cmd, 1f / 30f);
            clientState = clientSim.Step(clientState, cmd, 1f / 30f);
        }

        // Separate worlds with identical geometry must agree on the final position.
        Assert.Equal(serverState.Position.X, clientState.Position.X, 4);
        Assert.Equal(serverState.Position.Y, clientState.Position.Y, 4);
        Assert.Equal(serverState.Position.Z, clientState.Position.Z, 4);
    }

    // physics: null must leave movement terrain-only (unchanged from before). Steps 30 ticks for a robust check.
    [Fact]
    public void Simulator_WithNullPhysics_IsTerrainOnly()
    {
        var sim = new PlayerMoveSimulator(Flat, Tuning, physics: null);
        var state = new PlayerMoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        for (int i = 0; i < 30; i++)
            state = sim.Step(state, cmd, 1f / 30f);

        // Should have moved freely (no wall, flat terrain), ending well into positive Z.
        Assert.True(state.Position.Z > 0.5f,
            $"null physics should allow free movement over 30 ticks, z={state.Position.Z}");
        Assert.Equal(0.9f, state.Position.Y, 3);
    }
}
