using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// Walkable-prop-surface support through PlayerMoveSimulator was previously handled by WorldSurfaces.
// That path is now superseded by IPhysicsWorld (see Physics/NetWorldPhysicsTests.cs).
// These tests verify the null-physics (terrain-only) path is unchanged.
public class ServerSurfaceTests
{
    static float Flat(float x, float z) => 0f;

    [Fact]
    public void Simulator_NullPhysics_FallsToTerrainHeight()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, physics: null);
        var s = new PlayerMoveState { Move = new MoveState { Position = new Vector3(0f, 5f, 0f) } };
        for (int i = 0; i < 180; i++) s = sim.Step(s, default, 1f / 60f);
        // With flat terrain (height=0) and no physics world, capsule settles at CapsuleHalfHeight.
        Assert.Equal(MoveTuning.Default.CapsuleHalfHeight, s.Position.Y, 1);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void Server_ResolvesIdenticallyToClient()
    {
        var server = new PlayerMoveSimulator(Flat, MoveTuning.Default, physics: null);
        var client = new PlayerMoveSimulator(Flat, MoveTuning.Default, physics: null);
        var cmd = new MoveCommand(new Vector2(0.2f, 1f), run: false, cameraYaw: 0.3f);
        var a = new PlayerMoveState { Move = new MoveState { Position = new Vector3(0.1f, 3f, 0.1f) } };
        var b = a;
        for (int i = 0; i < 120; i++)
        {
            a = server.Step(a, cmd, 1f / 60f); b = client.Step(b, cmd, 1f / 60f);
            Assert.Equal(a.Position.Y, b.Position.Y, 5);
        }
    }
}
