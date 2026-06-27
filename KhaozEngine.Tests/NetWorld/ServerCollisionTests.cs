using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerCollisionTests
{
    static float Flat(float x, float z) => 0f;

    static WorldColliders OneTree() => new(new[] { WorldCollider.Cylinder(new Vector2(0f, -1.5f), 1f) });

    [Fact]
    public void Simulator_PushesPlayerOutOfTree()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, colliders: OneTree());
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // forward = -Z, into the tree
        var state = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 60; i++) state = sim.Step(state, cmd, 1f / 30f);
        float dist = new Vector2(state.Position.X, state.Position.Z + 1.5f).Length();
        Assert.True(dist >= 1.4f - 0.02f, $"server let player into tree: dist={dist}");
    }

    [Fact]
    public void Server_ResolvesIdenticallyToClientPrediction()
    {
        // The authoritative server (PlayerMoveSimulator) and the client's prediction both step the same
        // PlayerMoveSimulator instance configuration. Given identical colliders + commands they must match.
        var colliders = OneTree();
        var server = new PlayerMoveSimulator(Flat, MoveTuning.Default, colliders: colliders);
        var client = new PlayerMoveSimulator(Flat, MoveTuning.Default, colliders: colliders);
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
    public void Simulator_NoColliders_Unchanged()
    {
        var plain = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var withEmpty = new PlayerMoveSimulator(Flat, MoveTuning.Default, colliders: new WorldColliders(Array.Empty<WorldCollider>()));
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: false, cameraYaw: 0.3f);
        var a = new PlayerMoveState { Position = Vector3.Zero };
        var b = a;
        for (int i = 0; i < 20; i++) { a = plain.Step(a, cmd, 1f / 30f); b = withEmpty.Step(b, cmd, 1f / 30f); }
        Assert.Equal(a.Position.X, b.Position.X, 6);
        Assert.Equal(a.Position.Z, b.Position.Z, 6);
    }
}
