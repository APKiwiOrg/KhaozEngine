using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerSelfRescueTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    private static (WorldServer server, WorldClient client) Connect(WorldServerConfig config)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 20 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client);
    }

    private static void Pump(WorldServer s, WorldClient c, WorldServerConfig cfg, int n)
    {
        for (int i = 0; i < n; i++) { s.Poll(); s.Tick(cfg.TickSeconds); c.Poll(); }
    }

    private static PlayerMoveState State(WorldServer s)
    {
        int slot = s.JoinedSlots.First();
        Assert.True(s.TryGetPlayerState(slot, out PlayerMoveState st));
        return st;
    }

    [Fact]
    public void RequestSelfRescue_teleports_to_the_server_destination_and_zeroes_vertical_velocity()
    {
        // Elevated so the destination is airborne: grounding can't mask whether the teleport reset the velocity.
        var dest = new Vector3(40f, 25f, -30f);
        var config = new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            InterestRadius = 500f,
            MaxPlayers = 4,
            SelfRescueDestination = _ => dest,
        };
        (WorldServer server, WorldClient client) = Connect(config);

        // Jump so the player is rising fast (and off the destination).
        for (int i = 0; i < 3; i++)
        {
            client.SendInput(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true));
            Pump(server, client, config, 1);
        }
        Assert.True(State(server).VerticalVelocity > 1f, $"precondition: player should be rising, got {State(server).VerticalVelocity}");

        // The server owns the destination; the client only asks.
        Assert.True(client.RequestSelfRescue());
        for (int i = 0; i < 10; i++)
        {
            Pump(server, client, config, 1);
            if (MathF.Abs(State(server).Position.X - dest.X) < 1f) break;   // observed the teleport; read it now
        }

        PlayerMoveState after = State(server);
        Assert.Equal(40f, after.Position.X, 2);
        Assert.Equal(-30f, after.Position.Z, 2);
        Assert.True(after.Position.Y > 20f, $"should have teleported up to the destination, got Y {after.Position.Y}");
        Assert.True(after.VerticalVelocity < 1f, $"vertical velocity should be reset to ~0, got {after.VerticalVelocity}");
    }

    [Fact]
    public void RequestSelfRescue_is_a_no_op_when_no_destination_is_configured()
    {
        // Feature off by default (SelfRescueDestination null): the request is sent but the server ignores it.
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 4 };
        (WorldServer server, WorldClient client) = Connect(config);

        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 10; i++) { client.SendInput(forward); Pump(server, client, config, 1); }
        Vector3 before = State(server).Position;

        Assert.True(client.RequestSelfRescue());   // returns true (it was sent); the server has nowhere to send it
        Pump(server, client, config, 6);

        Vector3 after = State(server).Position;
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Z, after.Z, 2);
    }

    [Fact]
    public void RequestSelfRescue_respects_the_server_cooldown()
    {
        var dest = new Vector3(40f, 0f, -30f);
        var config = new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            InterestRadius = 500f,
            MaxPlayers = 4,
            SelfRescueDestination = _ => dest,
            SelfRescueCooldownSeconds = 1.5f,
        };
        (WorldServer server, WorldClient client) = Connect(config);
        int slot = server.JoinedSlots.First();

        // First rescue: honored.
        Assert.True(client.RequestSelfRescue());
        Pump(server, client, config, 6);
        Assert.Equal(40f, State(server).Position.X, 1);

        // Move the player elsewhere (deterministic, via the admin path) so a re-rescue would be visible.
        server.Teleport(PlayerRef.Slot(slot), new Vector3(5f, 0f, 5f));
        Pump(server, client, config, 4);
        Assert.Equal(5f, State(server).Position.X, 1);

        // Second rescue inside the cooldown window: denied (player stays put, not teleported back).
        Assert.True(client.RequestSelfRescue());
        Pump(server, client, config, 6);
        Assert.Equal(5f, State(server).Position.X, 1);

        // Wait out the cooldown, then rescue again: honored.
        Pump(server, client, config, 60);
        Assert.True(client.RequestSelfRescue());
        Pump(server, client, config, 6);
        Assert.Equal(40f, State(server).Position.X, 1);
    }

    [Fact]
    public void RequestSelfRescue_returns_false_when_not_connected()
    {
        var (_, ct) = LoopbackTransport.CreatePair();
        using var client = new WorldClient(ct, Flat, MoveTuning.Default);
        Assert.False(client.RequestSelfRescue());   // never polled to a server, so still Connecting
    }
}
