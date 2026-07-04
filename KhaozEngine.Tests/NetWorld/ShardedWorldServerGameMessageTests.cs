using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The multi-cell mirror of <see cref="WorldServerGameMessageTests"/>: the same generic game-message seam on
/// <see cref="ShardedWorldServer"/> (round trip both directions and both reliability modes, size cap + rate limit
/// hardening, slot-recycle isolation), confirming the sharded server shares the single-world semantics.
/// </summary>
public class ShardedWorldServerGameMessageTests
{
    static float Flat(float x, float z) => 0f;
    const NetChannelReliability Reliable = NetChannelReliability.ReliableOrdered;
    const NetChannelReliability Unrel = NetChannelReliability.UnreliableSequenced;

    static (ShardedWorldServer server, WorldClient client) Connect(ShardedWorldServerConfig config)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client);
    }

    static void Pump(ShardedWorldServer server, WorldClient client, float dt, int rounds)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); server.Tick(dt); client.Poll(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Client_game_message_reaches_the_server_OnGameMessage(bool reliable)
    {
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var (server, client) = Connect(config);

        var got = new List<(int slot, ushort kind, byte[] payload)>();
        server.OnGameMessage += (slot, kind, payload) => got.Add((slot, kind, payload.ToArray()));

        byte[] body = { 11, 22, 33 };
        Assert.True(client.SendGameMessage(0xCAFE, body, reliable ? Reliable : Unrel));
        Pump(server, client, config.TickSeconds, 5);

        Assert.Single(got);
        Assert.Equal(server.JoinedSlots.First(), got[0].slot);
        Assert.Equal(0xCAFE, got[0].kind);
        Assert.Equal(body, got[0].payload);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Server_game_message_reaches_the_client_GameMessageReceived(bool reliable)
    {
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var (server, client) = Connect(config);

        var got = new List<(ushort kind, byte[] payload)>();
        client.GameMessageReceived += (kind, payload) => got.Add((kind, payload.ToArray()));

        byte[] body = { 4, 5 };
        server.SendGameMessageTo(server.JoinedSlots.First(), 0x0BAD, body, reliable ? Reliable : Unrel);
        Pump(server, client, config.TickSeconds, 5);

        Assert.Single(got);
        Assert.Equal(0x0BAD, got[0].kind);
        Assert.Equal(body, got[0].payload);
    }

    [Fact]
    public void Broadcast_game_message_reaches_every_client()
    {
        var hub = new InMemoryHub();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var clientCfg = new WorldClientConfig { TickSeconds = config.TickSeconds };
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);
        for (int i = 0; i < 30 && !(a.Joined && b.Joined); i++)
        { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);

        ushort? gotA = null, gotB = null;
        a.GameMessageReceived += (k, _) => gotA = k;
        b.GameMessageReceived += (k, _) => gotB = k;

        server.BroadcastGameMessage(0x0101, new byte[] { 7 }, Reliable);
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }

        Assert.Equal((ushort)0x0101, gotA);
        Assert.Equal((ushort)0x0101, gotB);
    }

    [Fact]
    public void Oversize_game_message_is_flagged_and_never_dispatched()
    {
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8, MaxGameMessageBytes = 16 };
        var (server, client) = Connect(config);

        var flags = new List<SuspiciousActivity>();
        server.OnSuspiciousActivity += flags.Add;
        bool dispatched = false;
        server.OnGameMessage += (_, _, _) => dispatched = true;

        client.SendGameMessage(0x01, new byte[64], Reliable);
        Pump(server, client, config.TickSeconds, 5);

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.OversizedMessage && f.Magnitude == 64f);
        Assert.False(dispatched);
    }

    [Fact]
    public void Game_message_flood_trips_the_rate_limiter()
    {
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f,
            MaxPlayers = 8,
            AntiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 30f, MessageBurst = 3f },
        };
        var (server, client) = Connect(config);

        var flags = new List<SuspiciousActivity>();
        server.OnSuspiciousActivity += flags.Add;

        for (int i = 0; i < 8; i++) client.SendGameMessage(0x01, new byte[] { (byte)i }, Unrel);
        client.Poll();
        server.Poll();

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.RateLimited);
    }

    [Fact]
    public void Slot_recycle_does_not_leak_messages_across_occupants()
    {
        var hub = new InMemoryHub();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 1 };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var clientCfg = new WorldClientConfig { TickSeconds = config.TickSeconds };

        var got = new List<(int slot, ushort kind)>();
        server.OnGameMessage += (slot, kind, _) => got.Add((slot, kind));

        var ta = hub.CreateClient();
        var a = new WorldClient(ta, Flat, MoveTuning.Default, clientCfg);
        for (int i = 0; i < 20 && !a.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); }
        Assert.True(a.Joined);
        a.SendGameMessage(0xAAAA, new byte[] { 1 }, Reliable);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); }
        Assert.Single(got);
        int firstSlot = got[0].slot;

        hub.DisconnectClient(ta);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        Assert.Equal(0, server.PlayerCount);

        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);
        for (int i = 0; i < 20 && !b.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); b.Poll(); }
        Assert.True(b.Joined);
        b.SendGameMessage(0xBBBB, new byte[] { 2 }, Reliable);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); b.Poll(); }

        Assert.Equal(2, got.Count);
        Assert.Equal((ushort)0xAAAA, got[0].kind);
        Assert.Equal((ushort)0xBBBB, got[1].kind);
        Assert.Equal(firstSlot, got[1].slot);
    }
}
