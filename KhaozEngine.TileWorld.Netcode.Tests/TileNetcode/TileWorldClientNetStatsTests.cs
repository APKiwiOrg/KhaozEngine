using System;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// <see cref="TileWorldClient.NetStats"/>: the transport's own link health, read through the client rather than
/// off a transport reference a head has to keep for the purpose. The property owns no window and no counter, so
/// what has to hold is that it reads LIVE and adds nothing of its own.
/// </summary>
public class TileWorldClientNetStatsTests
{
    static TileWorldClient Client(INetTransport transport) =>
        new(transport, new TileWorldClientConfig { TickSeconds = 1f / 6f, StepTicks = new TileStepTicks(4, 2) },
            new TileCollisionMap(TileWorldDocument.DefaultPlaneCount));

    [Fact]
    public void NetStats_hands_back_the_transports_own_numbers_and_re_reads_them()
    {
        var transport = new StubTransport();
        using TileWorldClient client = Client(transport);

        // Before the transport has anything to say, the client says the same nothing rather than a shape of its own.
        Assert.False(client.NetStats.Connected);
        Assert.Equal(0f, client.NetStats.RttMs);

        transport.Stats = new NetTransportStats(connected: true, rttMs: 42.5f, packetLoss: 0.125f,
            bytesReceivedTotal: 1234, bytesSentTotal: 5678);

        NetTransportStats got = client.NetStats;
        Assert.True(got.Connected);
        Assert.Equal(42.5f, got.RttMs);
        Assert.Equal(0.125f, got.PacketLoss);
        Assert.Equal(1234L, got.BytesReceivedTotal);
        Assert.Equal(5678L, got.BytesSentTotal);

        // Live rather than captured at construction: the counters a HUD diffs have to move between reads.
        transport.Stats = new NetTransportStats(true, 40f, 0f, 2234, 6678);
        Assert.Equal(2234L, client.NetStats.BytesReceivedTotal);
        Assert.Equal(6678L, client.NetStats.BytesSentTotal);
    }

    [Fact]
    public void A_transport_that_tracks_nothing_reports_the_unavailable_value()
    {
        // The loopback case, and the reason the property cannot be read as "am I connected": every transport that
        // does not implement Stats answers the disconnected all-zero value while the session is perfectly alive.
        var transport = new SilentTransport();
        using TileWorldClient client = Client(transport);

        Assert.Equal(NetTransportStats.Unavailable.Connected, client.NetStats.Connected);
        Assert.Equal(NetTransportStats.Unavailable.RttMs, client.NetStats.RttMs);
        Assert.Equal(NetTransportStats.Unavailable.BytesSentTotal, client.NetStats.BytesSentTotal);
    }

    // A transport that does nothing but hold the stats the test hands it. The client never polls it here, which is
    // the point: NetStats is a read, not something a frame loop has to have pumped first.
    sealed class StubTransport : INetTransport
    {
        public NetTransportStats Stats { get; set; }
        public void Poll() { }
        public bool TryDequeueEvent(out NetEvent ev) { ev = default; return false; }
        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }
        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }

    // The same transport WITHOUT overriding Stats, so it takes the interface default.
    sealed class SilentTransport : INetTransport
    {
        public void Poll() { }
        public bool TryDequeueEvent(out NetEvent ev) { ev = default; return false; }
        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }
        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }
}
