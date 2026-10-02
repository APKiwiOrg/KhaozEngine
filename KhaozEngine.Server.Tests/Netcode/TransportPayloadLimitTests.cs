using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The optional packet-limit query on <see cref="INetTransport"/> and its session facades. Zero means unknown, which
/// is what an external transport that never implements the query reports, and what a facade reports for a
/// connection it does not hold. A facade forwards the connection id the transport actually assigned and the channel
/// it was asked about, never a guessed one.
/// </summary>
public class TransportPayloadLimitTests
{
    [Fact]
    public void DefaultTransportLimitIsUnknown()
    {
        INetTransport externalTransport = new ExternalTransport();
        var connection = new NetConnectionId(3);

        foreach (NetChannelReliability reliability in Reliabilities)
            Assert.Equal(0, externalTransport.MaxUnfragmentedPayloadBytes(connection, reliability));

        // The in-memory transports implement nothing, so they stay unknown by default too.
        (LoopbackTransport a, LoopbackTransport b) = LoopbackTransport.CreatePair();
        using (a)
        using (b)
            Assert.Equal(0, ((INetTransport)a).MaxUnfragmentedPayloadBytes(new NetConnectionId(1),
                NetChannelReliability.ReliableOrdered));

        // A facade over a transport that never answers forwards the same unknown.
        var client = new NetClient(externalTransport);
        ((ExternalTransport)externalTransport).Deliver(NetEvent.Connected(connection));
        client.Poll();
        Assert.Equal(0, client.MaxUnfragmentedPayloadBytes(NetChannelReliability.UnreliableSequenced));
    }

    [Fact]
    public void FacadeQueriesActualConnectionAndChannel()
    {
        var transport = new LimitTransport();
        var serverPeer = new NetConnectionId(7);   // deliberately not 1
        var client = new NetClient(transport);

        // No connection yet, so there is nothing to ask about and the transport is not asked.
        Assert.Equal(0, client.MaxUnfragmentedPayloadBytes(NetChannelReliability.ReliableOrdered));
        Assert.Empty(transport.Queries);

        transport.Deliver(NetEvent.Connected(serverPeer));
        client.Poll();

        Assert.Equal(LimitTransport.Limit(serverPeer, NetChannelReliability.UnreliableSequenced),
            client.MaxUnfragmentedPayloadBytes(NetChannelReliability.UnreliableSequenced));
        Assert.Equal(LimitTransport.Limit(serverPeer, NetChannelReliability.ReliableOrdered),
            client.MaxUnfragmentedPayloadBytes(NetChannelReliability.ReliableOrdered));
        Assert.Equal(new[]
        {
            (serverPeer, NetChannelReliability.UnreliableSequenced),
            (serverPeer, NetChannelReliability.ReliableOrdered),
        }, transport.Queries);
    }

    [Fact]
    public void ServerFacadeQueriesTheSlotsRenumberedConnection()
    {
        var transport = new LimitTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());
        var first = new NetConnectionId(5);
        var second = new NetConnectionId(9);
        transport.StageHello(first);
        transport.StageHello(second);
        server.Poll();

        Assert.Equal(LimitTransport.Limit(second, NetChannelReliability.UnreliableSequenced),
            server.MaxUnfragmentedPayloadBytes(1, NetChannelReliability.UnreliableSequenced));
        Assert.Equal(LimitTransport.Limit(first, NetChannelReliability.ReliableOrdered),
            server.MaxUnfragmentedPayloadBytes(0, NetChannelReliability.ReliableOrdered));
        Assert.Equal(new[]
        {
            (second, NetChannelReliability.UnreliableSequenced),
            (first, NetChannelReliability.ReliableOrdered),
        }, transport.Queries);
    }

    [Fact]
    public void AbsentConnectionReportsUnknownWithoutAskingTheTransport()
    {
        var transport = new LimitTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());
        var peer = new NetConnectionId(5);
        transport.StageHello(peer);
        server.Poll();

        Assert.Equal(0, server.MaxUnfragmentedPayloadBytes(3, NetChannelReliability.ReliableOrdered));
        Assert.Equal(0, server.MaxUnfragmentedPayloadBytes(-1, NetChannelReliability.UnreliableSequenced));
        Assert.Empty(transport.Queries);

        // A slot that left is absent again.
        transport.Deliver(NetEvent.Disconnected(peer));
        server.Poll();
        Assert.Equal(0, server.MaxUnfragmentedPayloadBytes(0, NetChannelReliability.ReliableOrdered));

        // A client whose session ended has no connection to ask about.
        var clientTransport = new LimitTransport();
        var client = new NetClient(clientTransport);
        clientTransport.Deliver(NetEvent.Connected(peer));
        clientTransport.Deliver(NetEvent.Disconnected(peer));
        client.Poll();
        Assert.Equal(0, client.MaxUnfragmentedPayloadBytes(NetChannelReliability.ReliableOrdered));
        Assert.Empty(clientTransport.Queries);
    }

    static readonly NetChannelReliability[] Reliabilities =
        { NetChannelReliability.UnreliableSequenced, NetChannelReliability.ReliableOrdered };

    // A transport written before the query existed: it implements only the required members.
    sealed class ExternalTransport : INetTransport
    {
        readonly Queue<NetEvent> inbound = new();

        public void Deliver(NetEvent ev) => inbound.Enqueue(ev);

        public void Poll() { }

        public bool TryDequeueEvent(out NetEvent ev) => inbound.TryDequeue(out ev);

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }

        public void Disconnect(NetConnectionId connection) { }

        public void Dispose() { }
    }

    // Answers a distinct limit per connection and channel and records every query.
    sealed class LimitTransport : INetTransport
    {
        readonly Queue<NetEvent> inbound = new();

        public List<(NetConnectionId Connection, NetChannelReliability Reliability)> Queries { get; } = new();

        public static int Limit(NetConnectionId connection, NetChannelReliability reliability) =>
            1000 + connection.Value * 10 + (int)reliability;

        public void Deliver(NetEvent ev) => inbound.Enqueue(ev);

        public void StageHello(NetConnectionId connection)
        {
            inbound.Enqueue(NetEvent.Connected(connection));
            inbound.Enqueue(NetEvent.FromData(connection, SessionFrame.Write(SessionOpcode.Hello, ReadOnlySpan<byte>.Empty),
                NetChannelReliability.ReliableOrdered));
        }

        public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability)
        {
            Queries.Add((connection, reliability));
            return Limit(connection, reliability);
        }

        public void Poll() { }

        public bool TryDequeueEvent(out NetEvent ev) => inbound.TryDequeue(out ev);

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }

        public void Disconnect(NetConnectionId connection) { }

        public void Dispose() { }
    }
}
