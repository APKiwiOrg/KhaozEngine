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

    // Server simulator and client simulator over the same world from the same initial state must produce identical trajectories.
    [Fact]
    public void TwoSimulators_OverSameWorld_ProduceIdenticalTrajectories()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        world.Step(1f / 30f);

        var serverSim = new PlayerMoveSimulator(Flat, Tuning, physics: world);
        var clientSim = new PlayerMoveSimulator(Flat, Tuning, physics: world);

        var initState = new PlayerMoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        PlayerMoveState serverState = initState;
        PlayerMoveState clientState = initState;

        for (int i = 0; i < 60; i++)
        {
            serverState = serverSim.Step(serverState, cmd, 1f / 30f);
            clientState = clientSim.Step(clientState, cmd, 1f / 30f);
        }

        // Server and client must agree on the final position.
        Assert.Equal(serverState.Position.X, clientState.Position.X, 4);
        Assert.Equal(serverState.Position.Y, clientState.Position.Y, 4);
        Assert.Equal(serverState.Position.Z, clientState.Position.Z, 4);
    }

    // physics: null must leave movement terrain-only (unchanged from before).
    [Fact]
    public void Simulator_WithNullPhysics_IsTerrainOnly()
    {
        var sim = new PlayerMoveSimulator(Flat, Tuning, physics: null);
        var state = new PlayerMoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        PlayerMoveState next = sim.Step(state, cmd, 1f / 30f);

        // Should move freely (no wall, flat terrain).
        Assert.True(next.Position.Z > state.Position.Z,
            $"null physics should allow free movement, z={next.Position.Z}");
        Assert.Equal(0.9f, next.Position.Y, 3);
    }
}
