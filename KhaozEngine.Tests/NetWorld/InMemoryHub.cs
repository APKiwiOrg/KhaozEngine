using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Deterministic in-memory multi-client transport for headless multi-client server tests: one server
/// endpoint fans out to N client endpoints, each a distinct <see cref="NetConnectionId"/>. Mirrors
/// LoopbackTransport's poll/drain model (a Send is surfaced on the peer's next Poll). No sockets, no threads.
/// </summary>
public sealed class InMemoryHub
{
    private readonly ServerEndpoint server;
    private readonly List<ClientEndpoint> clients = new();

    public InMemoryHub() => server = new ServerEndpoint(this);

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
    /// No-op for an unknown endpoint.</summary>
    public void DisconnectClient(INetTransport client)
    {
        if (client is not ClientEndpoint ce) return;
        int idx = clients.IndexOf(ce);
        if (idx < 0) return;
        server.OnClientRemoved(idx + 1);
        clients[idx].MarkDisconnected();
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
        private readonly InMemoryHub hub;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(int connId, byte[] data, NetChannelReliability r)> pending = new();
        private readonly Queue<int> newClients = new();
        private readonly Queue<int> goneClients = new();

        public ServerEndpoint(InMemoryHub hub) => this.hub = hub;

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

        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }

    private sealed class ClientEndpoint : INetTransport
    {
        private static readonly NetConnectionId ServerId = new(1);
        private readonly InMemoryHub hub;
        private readonly int connId;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(byte[] data, NetChannelReliability r)> pending = new();
        private bool announced;
        private bool disconnected;

        public ClientEndpoint(InMemoryHub hub, int connId) { this.hub = hub; this.connId = connId; }

        public void MarkDisconnected() { disconnected = true; pending.Clear(); }

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

        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }
}
