using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

// A rejecting server must reach the client as a terminal Rejected even over a transport where the reliable Reject
// frame is lost to an immediate teardown (the real LiteNetLib behaviour: peer.Disconnect() outruns the reliable
// flush). Before the fix the client saw only a bare drop and, under auto-reconnect, retried forever - the
// "reconnect never succeeds after a server restart, but a relaunch connects instantly" bug. The live-socket
// WorldClientLiveReconnectTests prove it end-to-end over real UDP; this headless model runs in CI.
public class RejectDeliveryTests
{
    [Fact]
    public void Reject_survives_a_lost_reliable_frame_via_the_disconnect_reason()
    {
        var (st, ct) = RejectLosingLoopback.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new DenyAll());
        var client = new NetClient(ct);

        for (int i = 0; i < 8; i++) { server.Poll(); client.Poll(); }

        // The reliable Reject Data frame was dropped by the teardown, but the disconnect carried the reason, so the
        // client still surfaces a terminal Rejected (not a bare Disconnected it would treat as a transient outage).
        var events = new List<ClientSessionEvent>();
        while (client.TryDequeueEvent(out ClientSessionEvent e)) events.Add(e);

        ClientSessionEvent rejected = Assert.Single(events, e => e.Kind == ClientSessionEventKind.Rejected);
        Assert.Equal("nope", rejected.RejectReason);
        Assert.DoesNotContain(events, e => e.Kind == ClientSessionEventKind.Joined);
        Assert.Equal(-1, client.Slot);
    }

    private sealed class DenyAll : IConnectionAuthenticator
    {
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            subject = string.Empty;
            rejectReason = "nope";
            return false;
        }
    }

    // A linked in-memory transport pair like LoopbackTransport, except a Disconnect DROPS the peer's not-yet-polled
    // inbound frames (modelling a real socket where an immediate teardown outruns the reliable flush, so a Reject
    // sent microseconds earlier never lands) while still delivering the disconnect and any reason it carries. This
    // is the headless stand-in for the LiteNetLib race the live-socket reconnect test hits.
    private sealed class RejectLosingLoopback : INetTransport
    {
        private static readonly NetConnectionId PeerId = new(1);
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(byte[] data, NetChannelReliability r)> pendingFromPeer = new();
        private RejectLosingLoopback? peer;
        private bool announced;

        public static (RejectLosingLoopback server, RejectLosingLoopback client) CreatePair()
        {
            var s = new RejectLosingLoopback();
            var c = new RejectLosingLoopback();
            s.peer = c;
            c.peer = s;
            return (s, c);
        }

        public void Poll()
        {
            if (!announced && peer is not null) { announced = true; inbox.Enqueue(NetEvent.Connected(PeerId)); }
            foreach ((byte[] data, NetChannelReliability r) in pendingFromPeer)
                inbox.Enqueue(NetEvent.FromData(PeerId, data, r));
            pendingFromPeer.Clear();
        }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
            peer?.pendingFromPeer.Add((payload.ToArray(), reliability));

        public void Disconnect(NetConnectionId connection) => Teardown(null);

        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) =>
            Teardown(reason.IsEmpty ? null : reason.ToArray());

        private void Teardown(byte[]? reason)
        {
            if (peer is null) return;
            peer.pendingFromPeer.Clear();   // the race: the un-flushed reliable Reject is lost to the teardown
            peer.inbox.Enqueue(NetEvent.Disconnected(PeerId, reason));
            peer.peer = null;
            peer = null;
        }

        public void Dispose() { peer = null; inbox.Clear(); pendingFromPeer.Clear(); }
    }
}
