using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerSurfaceTests
{
    static float Flat(float x, float z) => 0f;
    static PropSurface Slab(float y) { return new PropSurface(3, 3, 1f, -1.5f, -1.5f, new[] { y, y, y, y, y, y, y, y, y }); }
    static WorldSurfaces OneRock() => new(new[] { new WorldSurface(Slab(1.5f), Vector2.Zero, 1f, 0f, 0f) });

    [Fact]
    public void Simulator_StandsPlayerOnRock()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: OneRock());
        var s = new PlayerMoveState { Move = new MoveState { Position = new Vector3(0f, 5f, 0f) } };
        for (int i = 0; i < 180; i++) s = sim.Step(s, default, 1f / 60f);
        Assert.Equal(1.5f + MoveTuning.Default.CapsuleHalfHeight, s.Position.Y, 1);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void Server_ResolvesIdenticallyToClient()
    {
        var surfaces = OneRock();
        var server = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: surfaces);
        var client = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: surfaces);
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
