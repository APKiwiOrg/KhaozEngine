using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end tests for the generic game-message seam on the single-world <see cref="WorldServer"/>: round trip in
/// both directions and both reliability modes, the rate-limit + size-cap hardening, an old-style client ignoring the
/// new server frame, and slot-recycle isolation. The multi-cell mirror is
/// <see cref="ShardedWorldServerGameMessageTests"/>.
/// </summary>
public class WorldServerGameMessageTests
{
    static float Flat(float x, float z) => 0f;
    const NetChannelReliability Reliable = NetChannelReliability.ReliableOrdered;
    const NetChannelReliability Unrel = NetChannelReliability.UnreliableSequenced;

    static (WorldServer server, WorldClient client) Connect(WorldServerConfig config)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        Pump(server, client, config, 30);
        Assert.True(client.Joined);
        return (server, client);
    }

    static void Pump(WorldServer server, WorldClient client, WorldServerConfig config, int rounds)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
    }

    [Theory]
    [InlineData(true)]   // ReliableOrdered
    [InlineData(false)]  // UnreliableSequenced
    public void Client_game_message_reaches_the_server_OnGameMessage(bool reliable)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var (server, client) = Connect(config);

        var got = new List<(int slot, ushort kind, byte[] payload)>();
        server.OnGameMessage += (slot, kind, payload) => got.Add((slot, kind, payload.ToArray()));

        byte[] body = { 10, 20, 30, 40 };
        Assert.True(client.SendGameMessage(0x1234, body, reliable ? Reliable : Unrel));
        Pump(server, client, config, 5);

        Assert.Single(got);
        Assert.Equal(server.JoinedSlots.First(), got[0].slot);
        Assert.Equal(0x1234, got[0].kind);
        Assert.Equal(body, got[0].payload);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Server_game_message_reaches_the_client_GameMessageReceived(bool reliable)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var (server, client) = Connect(config);

        var got = new List<(ushort kind, byte[] payload)>();
        client.GameMessageReceived += (kind, payload) => got.Add((kind, payload.ToArray()));

        int slot = server.JoinedSlots.First();
        byte[] body = { 5, 6, 7 };
        server.SendGameMessageTo(slot, 0x77, body, reliable ? Reliable : Unrel);
        Pump(server, client, config, 5);

        Assert.Single(got);
        Assert.Equal(0x77, got[0].kind);
        Assert.Equal(body, got[0].payload);
    }

    [Fact]
    public void Broadcast_game_message_reaches_every_client()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var clientCfg = new WorldClientConfig { TickSeconds = config.TickSeconds };
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);

        for (int i = 0; i < 20 && !(a.Joined && b.Joined); i++)
        { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);

        ushort? gotA = null, gotB = null;
        a.GameMessageReceived += (k, _) => gotA = k;
        b.GameMessageReceived += (k, _) => gotB = k;

        server.BroadcastGameMessage(0x0F0F, new byte[] { 1 }, Reliable);
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }

        Assert.Equal((ushort)0x0F0F, gotA);
        Assert.Equal((ushort)0x0F0F, gotB);
    }

    [Fact]
    public void Client_send_forwards_the_chosen_reliability_channel_to_the_transport()
    {
        // Loopback delivers both channels identically, so round-trip alone can't prove the channel is honoured. Record
        // the raw transport sends and confirm the game-message frame went out UnreliableSequenced, not a hardcoded one.
        var (st, rawCt) = LoopbackTransport.CreatePair();
        var recording = new RecordingTransport(rawCt);
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(recording, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        // The transport-level payload is SessionFrame-wrapped, so decode it back out. Clearing right before the single
        // SendGameMessage means the only recorded send is that game message.
        recording.Sends.Clear();
        client.SendGameMessage(0x22, new byte[] { 1, 2, 3 }, Unrel);

        Assert.Single(recording.Sends);
        Assert.Equal(Unrel, recording.Sends[0].reliability);
    }

    [Fact]
    public void Server_send_forwards_the_chosen_reliability_channel_to_the_transport()
    {
        var (rawSt, ct) = LoopbackTransport.CreatePair();
        var recording = new RecordingTransport(rawSt);
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(recording, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        recording.Sends.Clear();
        server.SendGameMessageTo(server.JoinedSlots.First(), 0x33, new byte[] { 9 }, Unrel);

        // SendGameMessageTo is a single transport send; clearing first isolates it. It must carry the chosen channel.
        Assert.Single(recording.Sends);
        Assert.Equal(Unrel, recording.Sends[0].reliability);
    }

    [Fact]
    public void Oversize_game_message_is_flagged_and_never_dispatched()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4, MaxGameMessageBytes = 8 };
        var (server, client) = Connect(config);

        var flags = new List<SuspiciousActivity>();
        server.OnSuspiciousActivity += flags.Add;
        bool dispatched = false;
        server.OnGameMessage += (_, _, _) => dispatched = true;

        client.SendGameMessage(0x01, new byte[20], Reliable);   // 20 > 8-byte cap
        Pump(server, client, config, 5);

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.OversizedMessage && f.Magnitude == 20f);
        Assert.False(dispatched, "an over-cap game message must never reach the handler");
    }

    [Fact]
    public void At_cap_game_message_is_still_delivered()
    {
        // The cap is inclusive: a payload exactly at MaxGameMessageBytes is delivered, one byte over is dropped.
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4, MaxGameMessageBytes = 8 };
        var (server, client) = Connect(config);

        int dispatched = 0;
        server.OnGameMessage += (_, _, p) => { if (p.Length == 8) dispatched++; };

        client.SendGameMessage(0x01, new byte[8], Reliable);
        Pump(server, client, config, 5);
        Assert.Equal(1, dispatched);
    }

    [Fact]
    public void Game_message_flood_trips_the_rate_limiter()
    {
        var config = new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            MaxPlayers = 4,
            AntiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 30f, MessageBurst = 3f },
        };
        var (server, client) = Connect(config);

        var flags = new List<SuspiciousActivity>();
        server.OnSuspiciousActivity += flags.Add;

        for (int i = 0; i < 8; i++) client.SendGameMessage(0x01, new byte[] { (byte)i }, Unrel);
        client.Poll();
        server.Poll();   // one refill: burst of 3 pass, the rest are rate-limited

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.RateLimited);
    }

    [Fact]
    public void An_old_style_client_ignores_a_game_message_frame()
    {
        // RawDeltaClient's frame demux only handles Snapshot/Delta - exactly a client that predates game messages. It
        // must skip a GameMessage frame without throwing (version-skew-safe downstream), yet keep applying snapshots.
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 4, DeltaReplication = false };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var old = new RawDeltaClient(ct, MoveProtocol.CreateRegistry(), advertiseDelta: false);
        for (int i = 0; i < 20 && !old.Joined; i++) { old.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        Assert.True(old.Joined);

        server.BroadcastGameMessage(0x99, new byte[] { 1, 2, 3, 4 }, Reliable);
        // Should not throw; the old client keeps receiving and applying snapshots.
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); old.Poll(); }

        Assert.True(old.SnapshotFramesApplied > 0);
        Assert.True(old.LocalNetId >= 0);
    }

    [Fact]
    public void Slot_recycle_does_not_leak_messages_across_occupants()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 1 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var clientCfg = new WorldClientConfig { TickSeconds = config.TickSeconds };

        var got = new List<(int slot, ushort kind, byte[] payload)>();
        server.OnGameMessage += (slot, kind, payload) => got.Add((slot, kind, payload.ToArray()));

        // First occupant joins on slot 0 and sends a game message.
        var ta = hub.CreateClient();
        var a = new WorldClient(ta, Flat, MoveTuning.Default, clientCfg);
        for (int i = 0; i < 20 && !a.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); }
        Assert.True(a.Joined);
        a.SendGameMessage(0xAAAA, new byte[] { 1 }, Reliable);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); }
        Assert.Single(got);
        int firstSlot = got[0].slot;

        // Drop the first occupant; the server frees the slot.
        hub.DisconnectClient(ta);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        Assert.Equal(0, server.PlayerCount);

        // Second occupant recycles the same slot and sends its own game message.
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientCfg);
        for (int i = 0; i < 20 && !b.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); b.Poll(); }
        Assert.True(b.Joined);
        b.SendGameMessage(0xBBBB, new byte[] { 2 }, Reliable);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); b.Poll(); }

        // Exactly two dispatches total - the first occupant's message never re-fires after recycle.
        Assert.Equal(2, got.Count);
        Assert.Equal((ushort)0xAAAA, got[0].kind);
        Assert.Equal((ushort)0xBBBB, got[1].kind);
        Assert.Equal(firstSlot, got[1].slot);   // same recycled slot, attributed to the new occupant
    }
}
