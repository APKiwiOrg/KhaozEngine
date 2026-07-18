using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerSelfRescueTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    private static (ShardedWorldServer server, WorldClient client) Connect(ShardedWorldServerConfig config)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client);
    }

    private static PlayerMoveState State(ShardedWorldServer s)
    {
        int slot = s.JoinedSlots.First();
        Assert.True(s.TryGetPlayerState(slot, out PlayerMoveState st));
        return st;
    }

    [Fact]
    public void RequestSelfRescue_teleports_across_cells_and_zeroes_vertical_velocity()
    {
        // Destination is in a different cell (CellSize default 60) and elevated (airborne, so grounding can't mask
        // the velocity reset). The same WorldClient + MoveProtocol drive the multi-cell server unchanged.
        var dest = new Vector3(80f, 25f, -80f);
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f,
            MaxPlayers = 8,
            SelfRescueDestination = _ => dest,
        };
        (ShardedWorldServer server, WorldClient client) = Connect(config);

        for (int i = 0; i < 3; i++)
        {
            client.SendInput(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true));
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }
        Assert.True(State(server).VerticalVelocity > 1f, $"precondition: player should be rising, got {State(server).VerticalVelocity}");

        Assert.True(client.RequestSelfRescue());
        for (int i = 0; i < 12; i++)
        {
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            if (MathF.Abs(State(server).Position.X - dest.X) < 1f) break;
        }

        PlayerMoveState after = State(server);
        Assert.Equal(80f, after.Position.X, 2);
        Assert.Equal(-80f, after.Position.Z, 2);
        Assert.True(after.Position.Y > 20f, $"should have teleported up to the destination, got Y {after.Position.Y}");
        Assert.True(after.VerticalVelocity < 1f, $"vertical velocity should be reset to ~0, got {after.VerticalVelocity}");
    }

    [Fact]
    public void RequestSelfRescue_is_a_no_op_when_no_destination_is_configured()
    {
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        (ShardedWorldServer server, WorldClient client) = Connect(config);

        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 10; i++) { client.SendInput(forward); server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Vector3 before = State(server).Position;

        Assert.True(client.RequestSelfRescue());
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Vector3 after = State(server).Position;
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Z, after.Z, 2);
    }
}
