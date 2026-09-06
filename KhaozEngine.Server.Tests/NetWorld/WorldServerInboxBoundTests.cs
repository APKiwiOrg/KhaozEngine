using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// #786: the session inbox's cap and its drop counter reach the world servers, the same way #267's fix wired the
/// pending-connection pair through. Both surfaces are read off the WORLD server here, never off the inner
/// <see cref="NetServer"/>, because "the game can see it" is the whole point of the forwarding.
/// </summary>
public class WorldServerInboxBoundTests
{
    private static float Flat(float x, float z) => 0f;

    /// <summary>Surfaces a scripted batch of frames on the first Poll and nothing after, so ONE server Poll ingests
    /// the whole flood into the session inbox before the host drains a single event. No sockets, fully deterministic.</summary>
    private sealed class ScriptedTransport : INetTransport
    {
        private readonly Queue<NetEvent> staged = new();
        public void Stage(NetEvent ev) => staged.Enqueue(ev);
        public void Poll() { }
        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (staged.Count > 0) { ev = staged.Dequeue(); return true; }
            ev = default;
            return false;
        }
        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }
        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }

    private const int FloodFrames = 200;

    // A wire-wrapped Hello (one Joined) followed by FloodFrames data frames from the same connection, so the poll
    // enqueues FloodFrames + 1 events. Nothing here is terminal, so the cap applies to every one of them.
    private static ScriptedTransport Flood()
    {
        var transport = new ScriptedTransport();
        var conn = new NetConnectionId(1);
        transport.Stage(NetEvent.Connected(conn));
        transport.Stage(NetEvent.FromData(conn, SessionFrame.Write(SessionOpcode.Hello, TestHandshake.Wire("p1")),
            NetChannelReliability.ReliableOrdered));
        for (int i = 0; i < FloodFrames; i++)
            transport.Stage(NetEvent.FromData(conn, SessionFrame.Write(SessionOpcode.Data, new[] { (byte)(i & 0xFF) }),
                NetChannelReliability.UnreliableSequenced));
        return transport;
    }

    private static ShardedWorldServerConfig ShardedConfig(int? cap) => new()
    {
        TickSeconds = 1f / 30f,
        MaxPlayers = 4,
        CellSize = 60f,
        OverlapMargin = 24f,
        InterestRadius = 24f,
        MaxQueuedEvents = cap ?? BoundedEventQueue<ServerSessionEvent>.DefaultCapacity,
    };

    [Fact]
    public void WorldServer_ForwardsTheCap_AndCountsTheDrops()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4, MaxQueuedEvents = 8 };
        var server = new WorldServer(Flood(), config, Flat, MoveTuning.Default);

        Assert.Equal(0, server.DroppedEventCount);
        server.Poll();

        // 1 Joined + 200 Data enqueued against a cap of 8: everything but the newest 8 is evicted. A cap that never
        // reached the NetServer would leave this at 0.
        Assert.Equal(FloodFrames + 1 - 8, server.DroppedEventCount);
    }

    [Fact]
    public void WorldServer_DefaultCap_IsUnchanged_AndDropsNothing()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        Assert.Equal(BoundedEventQueue<ServerSessionEvent>.DefaultCapacity, config.MaxQueuedEvents);

        var server = new WorldServer(Flood(), config, Flat, MoveTuning.Default);
        server.Poll();

        Assert.Equal(0, server.DroppedEventCount);
    }

    [Fact]
    public void ShardedWorldServer_ForwardsTheCap_AndCountsTheDrops()
    {
        ShardedWorldServerConfig config = ShardedConfig(8);
        using var server = new ShardedWorldServer(Flood(), config, Flat, MoveTuning.Default);

        Assert.Equal(0, server.DroppedEventCount);
        server.Poll();

        Assert.Equal(FloodFrames + 1 - 8, server.DroppedEventCount);
    }

    [Fact]
    public void ShardedWorldServer_DefaultCap_IsUnchanged_AndDropsNothing()
    {
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, MaxPlayers = 4, CellSize = 60f, OverlapMargin = 24f, InterestRadius = 24f,
        };
        Assert.Equal(BoundedEventQueue<ServerSessionEvent>.DefaultCapacity, config.MaxQueuedEvents);

        using var server = new ShardedWorldServer(Flood(), config, Flat, MoveTuning.Default);
        server.Poll();

        Assert.Equal(0, server.DroppedEventCount);
    }

    // The exemption #130 bought: a Left never counts among the dropped, so a flood cannot make a departure
    // invisible. Two connections, the first leaves, then the second buries the Left under far more than the cap.
    [Fact]
    public void WorldServer_ALeftIsNeverCountedAmongTheDrops()
    {
        var transport = new ScriptedTransport();
        var leaver = new NetConnectionId(1);
        var flooder = new NetConnectionId(2);
        transport.Stage(NetEvent.Connected(leaver));
        transport.Stage(NetEvent.FromData(leaver, SessionFrame.Write(SessionOpcode.Hello, TestHandshake.Wire("a")),
            NetChannelReliability.ReliableOrdered));
        transport.Stage(NetEvent.Connected(flooder));
        transport.Stage(NetEvent.FromData(flooder, SessionFrame.Write(SessionOpcode.Hello, TestHandshake.Wire("b")),
            NetChannelReliability.ReliableOrdered));
        transport.Stage(NetEvent.Disconnected(leaver));
        for (int i = 0; i < FloodFrames; i++)
            transport.Stage(NetEvent.FromData(flooder, SessionFrame.Write(SessionOpcode.Data, new[] { (byte)(i & 0xFF) }),
                NetChannelReliability.UnreliableSequenced));

        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4, MaxQueuedEvents = 8 };
        var server = new WorldServer(transport, config, Flat, MoveTuning.Default);
        server.Poll();

        // 2 Joined + 200 Data are what the cap applies to. The Left rides outside it, so the count is 202 - 8 and
        // not 203 - 8.
        Assert.Equal(FloodFrames + 2 - 8, server.DroppedEventCount);
    }
}
