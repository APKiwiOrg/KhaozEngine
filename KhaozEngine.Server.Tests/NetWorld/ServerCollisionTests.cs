using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// Static-world collision through PlayerMoveSimulator is now handled by IPhysicsWorld (see Physics/NetWorldPhysicsTests.cs).
// These tests verify the null-physics (terrain-only) path - the pre-existing behaviour when no physics world is wired.
public class ServerCollisionTests
{
    static float Flat(float x, float z) => 0f;

    [Fact]
    public void Simulator_NullPhysics_MovesFreely()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        var state = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 60; i++) state = sim.Step(state, cmd, 1f / 30f);
        // No wall in null-physics mode: player moves freely in the -Z direction (forward = -Z at yaw 0).
        Assert.True(state.Position.Z < -0.5f, $"null-physics simulator should move freely: z={state.Position.Z}");
    }

    [Fact]
    public void Server_ResolvesIdenticallyToClientPrediction()
    {
        // Two simulators with identical configuration must produce identical results.
        var server = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var client = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var cmd = new MoveCommand(new Vector2(0.3f, 1f), run: false, cameraYaw: 0.5f);
        var s = new PlayerMoveState { Position = new Vector3(0.2f, 0f, 0.2f) };
        var c = s;
        for (int i = 0; i < 40; i++)
        {
            s = server.Step(s, cmd, 1f / 30f);
            c = client.Step(c, cmd, 1f / 30f);
            Assert.Equal(s.Position.X, c.Position.X, 6);
            Assert.Equal(s.Position.Z, c.Position.Z, 6);
        }
    }

    [Fact]
    public void Simulator_NullPhysics_IsTerrainOnly()
    {
        var plain = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var alsoPlain = new PlayerMoveSimulator(Flat, MoveTuning.Default, physics: null);
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: false, cameraYaw: 0.3f);
        var a = new PlayerMoveState { Position = Vector3.Zero };
        var b = a;
        for (int i = 0; i < 20; i++) { a = plain.Step(a, cmd, 1f / 30f); b = alsoPlain.Step(b, cmd, 1f / 30f); }
        Assert.Equal(a.Position.X, b.Position.X, 6);
        Assert.Equal(a.Position.Z, b.Position.Z, 6);
    }
}
