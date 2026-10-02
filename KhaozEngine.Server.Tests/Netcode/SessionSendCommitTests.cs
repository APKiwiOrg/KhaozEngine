using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The success-reporting session sends. True means the frame was handed to the transport for a connection the
/// facade holds, never that it was delivered. False means there was no connection, and then nothing reached the
/// transport. A send that throws propagates unchanged, so it can never be read as a commitment.
/// </summary>
public class SessionSendCommitTests
{
    static readonly byte[] Payload = { 4, 2, 9 };

    [Fact]
    public void MissingSlotDoesNotCommitSend()
    {
        var transport = new CommitTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());
        var peer = new NetConnectionId(6);
        transport.StageHello(peer);
        server.Poll();
        transport.Sent.Clear();   // drop the Welcome
        const int missingSlot = 2;
        NetChannelReliability reliability = NetChannelReliability.UnreliableSequenced;

        Assert.False(server.TrySendTo(missingSlot, Payload, reliability));
        Assert.Empty(transport.Sent);

        Assert.True(server.TrySendTo(0, Payload, reliability));
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(peer, sent.Target);
        Assert.Equal(reliability, sent.Reliability);
        Assert.Equal(SessionOpcode.Data, SessionFrame.ReadOpcode(sent.Payload));
        Assert.Equal(Payload, SessionFrame.ReadBody(sent.Payload));

        // The void form still forwards and still ignores a missing slot silently.
        server.SendTo(missingSlot, Payload, reliability);
        server.SendTo(0, Payload, NetChannelReliability.ReliableOrdered);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(NetChannelReliability.ReliableOrdered, transport.Sent[1].Reliability);

        // A slot that left is missing again.
        transport.Deliver(NetEvent.Disconnected(peer));
        server.Poll();
        Assert.False(server.TrySendTo(0, Payload, reliability));
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public void SendExceptionCannotCommit()
    {
        var transport = new CommitTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());
        var peer = new NetConnectionId(6);
        transport.StageHello(peer);
        server.Poll();

        var clientTransport = new CommitTransport();
        var client = new NetClient(clientTransport);
        clientTransport.Deliver(NetEvent.Connected(peer));
        client.Poll();

        var fault = new InvalidOperationException("transport fault");
        transport.Fault = fault;
        clientTransport.Fault = fault;

        Assert.Same(fault, Assert.Throws<InvalidOperationException>(
            () => server.TrySendTo(0, Payload, NetChannelReliability.ReliableOrdered)));
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(
            () => client.TrySend(Payload, NetChannelReliability.ReliableOrdered)));
        Assert.Equal(1, transport.SendCalls - transport.Sent.Count);         // called exactly once, never recorded
        Assert.Equal(2, clientTransport.SendCalls);                          // the Hello, then the one faulted call
        Assert.Single(clientTransport.Sent);                                 // only the Hello completed

        // The void forms propagate the same fault rather than swallowing it.
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(
            () => server.SendTo(0, Payload, NetChannelReliability.ReliableOrdered)));
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(
            () => client.Send(Payload, NetChannelReliability.ReliableOrdered)));
    }

    [Fact]
    public void ClientSendCommitsOnlyWhileConnected()
    {
        var transport = new CommitTransport();
        var client = new NetClient(transport);
        var serverPeer = new NetConnectionId(7);

        Assert.False(client.TrySend(Payload, NetChannelReliability.ReliableOrdered));
        Assert.Empty(transport.Sent);

        transport.Deliver(NetEvent.Connected(serverPeer));
        client.Poll();
        transport.Sent.Clear();   // drop the Hello

        Assert.True(client.TrySend(Payload, NetChannelReliability.UnreliableSequenced));
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(serverPeer, sent.Target);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, sent.Reliability);
        Assert.Equal(Payload, SessionFrame.ReadBody(sent.Payload));

        transport.Deliver(NetEvent.Disconnected(serverPeer));
        client.Poll();
        Assert.False(client.TrySend(Payload, NetChannelReliability.ReliableOrdered));
        Assert.Single(transport.Sent);
    }

    [Fact]
    public void DisconnectTargetsTheServerPeerAndEndsSendEligibility()
    {
        var transport = new CommitTransport();
        var client = new NetClient(transport);
        var serverPeer = new NetConnectionId(7);

        client.Disconnect();   // nothing to disconnect yet
        Assert.Empty(transport.Disconnects);

        transport.Deliver(NetEvent.Connected(serverPeer));
        client.Poll();
        transport.Sent.Clear();

        client.Disconnect();
        Assert.Equal(new[] { serverPeer }, transport.Disconnects);
        Assert.False(client.TrySend(Payload, NetChannelReliability.ReliableOrdered));
        Assert.Empty(transport.Sent);
        Assert.Equal(0, client.MaxUnfragmentedPayloadBytes(NetChannelReliability.ReliableOrdered));

        client.Disconnect();   // the connection is already cleared, so a second call does not reach the transport
        Assert.Single(transport.Disconnects);

        // The transport's own Disconnected still surfaces as the session's end.
        transport.Deliver(NetEvent.Disconnected(serverPeer));
        client.Poll();
        Assert.True(client.TryDequeueEvent(out ClientSessionEvent ev));
        Assert.Equal(ClientSessionEventKind.Disconnected, ev.Kind);
        Assert.Equal(-1, client.Slot);
    }

    // Records completed sends, counts every call and throws a staged fault from inside Send.
    sealed class CommitTransport : INetTransport
    {
        readonly Queue<NetEvent> inbound = new();

        public List<(NetConnectionId Target, byte[] Payload, NetChannelReliability Reliability)> Sent { get; } = new();

        public List<NetConnectionId> Disconnects { get; } = new();

        public int SendCalls { get; private set; }

        public Exception? Fault { get; set; }

        public void Deliver(NetEvent ev) => inbound.Enqueue(ev);

        public void StageHello(NetConnectionId connection)
        {
            inbound.Enqueue(NetEvent.Connected(connection));
            inbound.Enqueue(NetEvent.FromData(connection, SessionFrame.Write(SessionOpcode.Hello, ReadOnlySpan<byte>.Empty),
                NetChannelReliability.ReliableOrdered));
        }

        public void Poll() { }

        public bool TryDequeueEvent(out NetEvent ev) => inbound.TryDequeue(out ev);

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            SendCalls++;
            if (Fault is not null) throw Fault;
            Sent.Add((target, payload.ToArray(), reliability));
        }

        public void Disconnect(NetConnectionId connection) => Disconnects.Add(connection);

        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => Disconnects.Add(connection);

        public void Dispose() { }
    }
}
