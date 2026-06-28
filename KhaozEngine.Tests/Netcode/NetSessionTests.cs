using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class NetSessionTests
{
    [Fact]
    public void Frame_RoundTrips_OpcodeAndBody()
    {
        byte[] framed = SessionFrame.Write(SessionOpcode.Data, new byte[] { 5, 6, 7 });
        SessionOpcode op = SessionFrame.ReadOpcode(framed);
        byte[] body = SessionFrame.ReadBody(framed);
        Assert.Equal(SessionOpcode.Data, op);
        Assert.Equal(new byte[] { 5, 6, 7 }, body);
    }

    [Fact]
    public void Frame_EmptyOrUnknown_IsSafe()
    {
        Assert.Equal(SessionOpcode.Unknown, SessionFrame.ReadOpcode(Array.Empty<byte>()));
        Assert.Equal(SessionOpcode.Unknown, SessionFrame.ReadOpcode(new byte[] { 0xFF }));
    }

    // Pumps both ends a few rounds so the loopback handshake settles.
    private static void Pump(NetServer server, NetClient client, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); client.Poll(); }
    }

    private static List<ClientSessionEvent> DrainClient(NetClient c)
    {
        var list = new List<ClientSessionEvent>();
        while (c.TryDequeueEvent(out ClientSessionEvent e)) list.Add(e);
        return list;
    }

    private static List<ServerSessionEvent> DrainServer(NetServer s)
    {
        var list = new List<ServerSessionEvent>();
        while (s.TryDequeueEvent(out ServerSessionEvent e)) list.Add(e);
        return list;
    }

    [Fact]
    public void Client_Handshakes_JoinsSlot0_AndExchangesData()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new AllowAllAuthenticator());
        var client = new NetClient(ct);

        Pump(server, client);

        Assert.Contains(DrainServer(server), e => e.Kind == ServerSessionEventKind.Joined && e.Slot == 0);
        Assert.Contains(DrainClient(client), e => e.Kind == ClientSessionEventKind.Joined && e.Slot == 0);
        Assert.Equal(0, client.Slot);

        // server -> client data
        server.SendTo(0, new byte[] { 99 }, NetChannelReliability.ReliableOrdered);
        Pump(server, client);
        Assert.Contains(DrainClient(client), e => e.Kind == ClientSessionEventKind.Data && e.Data.Length == 1 && e.Data[0] == 99);

        // client -> server data
        client.Send(new byte[] { 7, 7 }, NetChannelReliability.UnreliableSequenced);
        Pump(server, client);
        Assert.Contains(DrainServer(server), e => e.Kind == ServerSessionEventKind.Data && e.Slot == 0 && e.Data.Length == 2);
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

    [Fact]
    public void Client_RejectedByAuthenticator_GetsReason_NoSlot()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new DenyAll());
        var client = new NetClient(ct);

        Pump(server, client);

        Assert.DoesNotContain(DrainServer(server), e => e.Kind == ServerSessionEventKind.Joined);
        var rejected = Assert.Single(DrainClient(client), e => e.Kind == ClientSessionEventKind.Rejected);
        Assert.Equal("nope", rejected.RejectReason);
        Assert.Equal(-1, client.Slot);
    }
}
