using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The global pending-connection cap and the two counters that make a connection flood visible. A pending connection
/// is one the transport accepted that holds no slot yet: connected, Hello not yet answered. Before this, a Connected
/// event was a no-op with nothing to count and nothing to bound, and the only cap in the stack (the per-connection
/// rate limiter) does not engage until a slot exists.
/// </summary>
public class NetServerPendingConnectionTests
{
    private static byte[] Hello() => SessionFrame.Write(SessionOpcode.Hello, Array.Empty<byte>());

    private sealed class DenyAll : IConnectionAuthenticator
    {
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            subject = string.Empty;
            rejectReason = "nope";
            return false;
        }
    }

    [Fact]
    public void Connects_are_counted_while_they_hold_no_slot()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());

        transport.Deliver(NetEvent.Connected(new NetConnectionId(1)));
        transport.Deliver(NetEvent.Connected(new NetConnectionId(2)));
        server.Poll();
        Assert.Equal(2, server.PendingConnectionCount);

        // A Hello takes a slot, so that connection stops being pending.
        transport.Deliver(NetEvent.FromData(new NetConnectionId(1), Hello(), NetChannelReliability.ReliableOrdered));
        server.Poll();
        Assert.Equal(1, server.PendingConnectionCount);

        // A drop clears the other one.
        transport.Deliver(NetEvent.Disconnected(new NetConnectionId(2)));
        server.Poll();
        Assert.Equal(0, server.PendingConnectionCount);
    }

    [Fact]
    public void Uncapped_by_default_so_the_pre_cap_behaviour_is_unchanged()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());

        for (int i = 1; i <= 200; i++) transport.Deliver(NetEvent.Connected(new NetConnectionId(i)));
        server.Poll();

        Assert.Equal(200, server.PendingConnectionCount);
        Assert.Equal(0, server.RefusedPendingConnectionCount);
        Assert.Empty(transport.Disconnects);
    }

    [Fact]
    public void A_flood_past_the_cap_is_refused_and_counted()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 64, new AllowAllAuthenticator(), maxPendingConnections: 3);

        for (int i = 1; i <= 50; i++) transport.Deliver(NetEvent.Connected(new NetConnectionId(i)));
        server.Poll();

        Assert.Equal(3, server.PendingConnectionCount);          // never grew past the cap
        Assert.Equal(47, server.RefusedPendingConnectionCount);
        Assert.Equal(47, transport.Disconnects.Count);
        // A bare disconnect, deliberately: a cap whose job is to shed a flood must not answer every flooded connect
        // with a framed Reject of its own.
        Assert.All(transport.Disconnects, d => Assert.Null(d.Reason));
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void A_refused_connect_frees_capacity_for_the_next_one()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 64, new AllowAllAuthenticator(), maxPendingConnections: 1);

        transport.Deliver(NetEvent.Connected(new NetConnectionId(1)));
        transport.Deliver(NetEvent.Connected(new NetConnectionId(2)));   // refused, the cap is 1
        server.Poll();
        Assert.Equal(1, server.RefusedPendingConnectionCount);

        // The holder joins, which releases the pending seat, so the next connect is admitted rather than refused.
        transport.Deliver(NetEvent.FromData(new NetConnectionId(1), Hello(), NetChannelReliability.ReliableOrdered));
        transport.Deliver(NetEvent.Connected(new NetConnectionId(3)));
        server.Poll();

        Assert.Equal(1, server.PendingConnectionCount);
        Assert.Equal(1, server.RefusedPendingConnectionCount);   // unchanged: 3 was admitted
    }

    /// <summary>A refused HELLO is torn down here rather than by a transport event, so its pending seat has to be
    /// released here too. A transport that tells only the PEER about a disconnect it was asked to make (the in-memory
    /// loopback is one) surfaces no Disconnected for it, and the seat would leak for the process's lifetime.</summary>
    [Fact]
    public void A_rejected_hello_releases_its_pending_seat()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 64, new DenyAll(), maxPendingConnections: 2);

        transport.Deliver(NetEvent.Connected(new NetConnectionId(1)));
        transport.Deliver(NetEvent.FromData(new NetConnectionId(1), Hello(), NetChannelReliability.ReliableOrdered));
        server.Poll();

        Assert.Equal(0, server.PendingConnectionCount);
        Assert.Equal(0, server.RefusedPendingConnectionCount);
    }

    [Fact]
    public void A_negative_cap_is_rejected()
    {
        using var transport = new RecordingTransport();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator(), maxPendingConnections: -1));
    }

    /// <summary>The kick seam carries a reason now. Without it a kicked client saw a bare drop, read it as a
    /// transient outage and reconnected into the same kick, which is the failure the Hello-rejection path already
    /// solved by riding the reason on the disconnect itself.</summary>
    [Fact]
    public void A_kick_with_a_reason_puts_it_on_the_disconnect_and_on_a_reliable_frame()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());

        transport.Deliver(NetEvent.Connected(new NetConnectionId(1)));
        transport.Deliver(NetEvent.FromData(new NetConnectionId(1), Hello(), NetChannelReliability.ReliableOrdered));
        server.Poll();
        transport.Sent.Clear();   // drop the Welcome

        server.Disconnect(slot: 0, reason: "ke:flood-kick");

        (NetConnectionId target, byte[]? reason) = Assert.Single(transport.Disconnects);
        Assert.Equal(new NetConnectionId(1), target);
        Assert.NotNull(reason);
        Assert.Equal(SessionOpcode.Reject, SessionFrame.ReadOpcode(reason!));
        Assert.Equal("ke:flood-kick", Encoding.UTF8.GetString(SessionFrame.ReadBody(reason!)));

        // And the same framed Reject went out reliably, so a lossless transport delivers it ahead of the drop.
        (NetConnectionId sentTo, byte[] payload, NetChannelReliability reliability) = Assert.Single(transport.Sent);
        Assert.Equal(new NetConnectionId(1), sentTo);
        Assert.Equal(NetChannelReliability.ReliableOrdered, reliability);
        Assert.Equal("ke:flood-kick", Encoding.UTF8.GetString(SessionFrame.ReadBody(payload)));
    }

    [Fact]
    public void A_kick_with_a_reason_on_an_unknown_slot_is_a_no_op()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());

        server.Disconnect(slot: 3, reason: "ke:flood-kick");

        Assert.Empty(transport.Disconnects);
        Assert.Empty(transport.Sent);
    }
}
