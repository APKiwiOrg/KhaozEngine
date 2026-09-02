using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Deterministic in-memory multi-client transport for headless multi-client server tests: one server
/// endpoint fans out to N client endpoints, each a distinct <see cref="NetConnectionId"/>. Mirrors
/// LoopbackTransport's poll/drain model (a Send is surfaced on the peer's next Poll). No sockets, no threads.
/// <para>Deliberately a COPY of KhaozEngine.Server.Tests/NetWorld/InMemoryHub.cs rather than a reference to it.
/// Both are test helpers in test projects, and the only way to reach that one is a ProjectReference to
/// KhaozEngine.Server.Tests, which drags its whole closure in: 37 projects, 24 of them ones this suite does not
/// already carry, NetWorld among them. Push CI selects affected test projects by the reference graph, and
/// ArchitectureTests.TileWorldNetcode_NeverReferencesNetWorld holds this suite to the tile package's own narrow
/// graph (16 projects), so the copy is what keeps both true. Physics is NOT one of the projects such a reference
/// would add, because it already arrives here through Sharding. If a third suite ever needs this hub, promote it
/// into KhaozEngine.Netcode beside LoopbackTransport instead of copying it again.</para>
/// </summary>
public sealed class InMemoryTransportHub
{
    private readonly ServerEndpoint server;
    private readonly List<ClientEndpoint> clients = new();

    public InMemoryTransportHub() => server = new ServerEndpoint(this);

    /// <summary>The single server transport (hand to a NetServer / WorldServer).</summary>
    public INetTransport Server => server;

    /// <summary>Adds a client endpoint with a fresh connection id (hand to a NetClient / WorldClient).</summary>
    public INetTransport CreateClient()
    {
        int connId = clients.Count + 1;            // distinct positive id per client
        var c = new ClientEndpoint(this, connId);
        clients.Add(c);
        server.OnClientAdded(connId);
        return c;
    }

    /// <summary>Surfaces a disconnect for a client endpoint on the server's next Poll (the server then frees the
    /// player slot, which the next client to join recycles). The endpoint stops sending/receiving afterwards.
    /// No-op for an unknown endpoint, and IDEMPOTENT: both endpoints' own Disconnect routes here, so a test that
    /// kicks and then drops the same link explicitly must not see two Disconnected events.</summary>
    public void DisconnectClient(INetTransport client)
    {
        if (client is not ClientEndpoint ce) return;
        int idx = clients.IndexOf(ce);
        if (idx < 0 || ce.IsDisconnected) return;
        server.OnClientRemoved(idx + 1);
        clients[idx].MarkDisconnected();
    }

    // The server side of INetTransport.Disconnect, by connection id rather than by endpoint.
    private void DisconnectConnection(int connId)
    {
        if (connId - 1 >= 0 && connId - 1 < clients.Count) DisconnectClient(clients[connId - 1]);
    }

    private void ServerSend(int connId, byte[] data, NetChannelReliability r)
    {
        if (connId - 1 >= 0 && connId - 1 < clients.Count)
            clients[connId - 1].EnqueueFromServer(data, r);   // dropped if that endpoint has disconnected
    }

    private void ClientSend(int connId, byte[] data, NetChannelReliability r) =>
        server.EnqueueFromClient(connId, data, r);

    private sealed class ServerEndpoint : INetTransport
    {
        private readonly InMemoryTransportHub hub;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(int connId, byte[] data, NetChannelReliability r)> pending = new();
        private readonly Queue<int> newClients = new();
        private readonly Queue<int> goneClients = new();

        public ServerEndpoint(InMemoryTransportHub hub) => this.hub = hub;

        public void OnClientAdded(int connId) => newClients.Enqueue(connId);

        public void OnClientRemoved(int connId) => goneClients.Enqueue(connId);

        public void EnqueueFromClient(int connId, byte[] data, NetChannelReliability r) =>
            pending.Add((connId, data, r));

        public void Poll()
        {
            while (newClients.Count > 0)
                inbox.Enqueue(NetEvent.Connected(new NetConnectionId(newClients.Dequeue())));
            foreach ((int connId, byte[] data, NetChannelReliability r) in pending)
                inbox.Enqueue(NetEvent.FromData(new NetConnectionId(connId), data, r));
            pending.Clear();
            while (goneClients.Count > 0)
                inbox.Enqueue(NetEvent.Disconnected(new NetConnectionId(goneClients.Dequeue())));
        }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
            hub.ServerSend(target.Value, payload.ToArray(), reliability);

        // A real drop, like LoopbackTransport.Disconnect: a head that kicks a client is exercised end to end here
        // rather than compensated for with an explicit hub.DisconnectClient beside every kick.
        public void Disconnect(NetConnectionId connection) => hub.DisconnectConnection(connection.Value);
        public void Dispose() { }
    }

    private sealed class ClientEndpoint : INetTransport
    {
        private static readonly NetConnectionId ServerId = new(1);
        private readonly InMemoryTransportHub hub;
        private readonly int connId;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(byte[] data, NetChannelReliability r)> pending = new();
        private bool announced;
        private bool disconnected;

        public ClientEndpoint(InMemoryTransportHub hub, int connId) { this.hub = hub; this.connId = connId; }

        public bool IsDisconnected => disconnected;

        public void MarkDisconnected()
        {
            disconnected = true;
            // Everything already sent still surfaces, ahead of the drop, which is LoopbackTransport.Disconnect's
            // own promise that a plain kick loses nothing. It is load-bearing for a kick: the reason notice is
            // Sent and the link dropped in the same call, so clearing the queue here would swallow the reason and
            // the client would learn only that it was gone.
            foreach ((byte[] data, NetChannelReliability r) in pending) inbox.Enqueue(NetEvent.FromData(ServerId, data, r));
            pending.Clear();
            inbox.Enqueue(NetEvent.Disconnected(ServerId));
        }

        public void EnqueueFromServer(byte[] data, NetChannelReliability r)
        {
            if (!disconnected) pending.Add((data, r));
        }

        public void Poll()
        {
            if (disconnected) { pending.Clear(); return; }
            if (!announced) { announced = true; inbox.Enqueue(NetEvent.Connected(ServerId)); }
            foreach ((byte[] data, NetChannelReliability r) in pending)
                inbox.Enqueue(NetEvent.FromData(ServerId, data, r));
            pending.Clear();
        }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            if (!disconnected) hub.ClientSend(connId, payload.ToArray(), reliability);
        }

        // The client hanging up on the server, routed through the same one place so both ends stay in step.
        public void Disconnect(NetConnectionId connection) => hub.DisconnectClient(this);
        public void Dispose() { }
    }
}
