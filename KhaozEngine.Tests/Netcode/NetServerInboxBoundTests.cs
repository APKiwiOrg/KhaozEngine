using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class NetServerInboxBoundTests
{
    /// <summary>A transport that surfaces a scripted batch of events on the first Poll and nothing after,
    /// so a host that never drains can be flooded deterministically with no sockets.</summary>
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

    private static List<ServerSessionEvent> Drain(NetServer s)
    {
        var list = new List<ServerSessionEvent>();
        while (s.TryDequeueEvent(out ServerSessionEvent e)) list.Add(e);
        return list;
    }

    [Fact]
    public void FloodedInbox_NeverExceedsCap_DropsOldest_KeepsNewest_AndCounts()
    {
        var transport = new ScriptedTransport();
        var conn = new NetConnectionId(1);
        // Establish a slot with a Hello (enqueues one Joined into the inbox).
        transport.Stage(NetEvent.FromData(conn, SessionFrame.Write(SessionOpcode.Hello, Array.Empty<byte>()),
            NetChannelReliability.ReliableOrdered));
        // Then flood 200 data frames the host will never drain. Each carries a 1-byte sequence tag.
        const int floods = 200;
        for (int i = 0; i < floods; i++)
            transport.Stage(NetEvent.FromData(conn, SessionFrame.Write(SessionOpcode.Data, new[] { (byte)(i & 0xFF) }),
                NetChannelReliability.UnreliableSequenced));

        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator(), maxQueuedEvents: 8);
        server.Poll(); // ingests Hello + 200 data, never draining

        // 1 Joined + 200 Data = 201 enqueued; cap 8 means 193 evicted (oldest-first).
        Assert.Equal(201 - 8, server.DroppedEventCount);

        List<ServerSessionEvent> drained = Drain(server);
        Assert.Equal(8, drained.Count); // never grew past the cap
        // The oldest (the Joined, then early data) were dropped; only the newest 8 data frames remain.
        Assert.All(drained, e => Assert.Equal(ServerSessionEventKind.Data, e.Kind));
        var tags = drained.ConvertAll(e => e.Data[0]);
        Assert.Equal(new byte[] { 192, 193, 194, 195, 196, 197, 198, 199 }, tags);
    }
}
