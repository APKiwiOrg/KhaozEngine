using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// A deterministic in-memory transport hub for one server and multiple clients. It uses no sockets or threads.
/// <see cref="CreateClient"/> assigns each client a distinct positive server-side connection id. Sends are copied
/// and staged until the receiving endpoint's next <see cref="INetTransport.Poll"/>, preserving call order.
/// Intended for headless tests and single-process local hosts that need more than
/// <see cref="LoopbackTransport.CreatePair"/>'s one peer.
/// </summary>
public sealed class InMemoryTransportHub : IDisposable
{
    private readonly ServerEndpoint server;
    private readonly List<ClientEndpoint> clients = new();
    private bool disposed;

    /// <summary>Creates an empty hub with one server endpoint.</summary>
    public InMemoryTransportHub() => server = new ServerEndpoint(this);

    /// <summary>The single server transport.</summary>
    public INetTransport Server => server;

    /// <summary>Creates one connected client endpoint with a fresh connection id.</summary>
    /// <exception cref="ObjectDisposedException">The hub or its server endpoint has been disposed.</exception>
    public INetTransport CreateClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int connectionId = clients.Count + 1;
        var client = new ClientEndpoint(this, connectionId);
        clients.Add(client);
        server.AddClient(connectionId);
        return client;
    }

    /// <summary>
    /// Disconnects a client created by this hub. Data already sent in either direction remains ordered before each
    /// side's terminal event. Unknown, disposed, or already disconnected endpoints are ignored.
    /// </summary>
    public void DisconnectClient(INetTransport client)
    {
        if (client is not ClientEndpoint endpoint) return;
        int index = clients.IndexOf(endpoint);
        if (index < 0) return;
        Disconnect(index + 1, ReadOnlySpan<byte>.Empty);
    }

    private void Disconnect(int connectionId, ReadOnlySpan<byte> reason)
    {
        int index = connectionId - 1;
        if ((uint)index >= (uint)clients.Count) return;
        ClientEndpoint client = clients[index];
        if (!client.IsConnected) return;
        bool supersede = !reason.IsEmpty;
        byte[] reasonCopy = reason.ToArray();
        server.RemoveClient(connectionId, reasonCopy, supersede);
        client.DisconnectFromServer(reasonCopy, supersede);
    }

    private void SendToServer(int connectionId, byte[] payload, NetChannelReliability reliability) =>
        server.EnqueueData(connectionId, payload, reliability);

    private void SendToClient(int connectionId, byte[] payload, NetChannelReliability reliability)
    {
        int index = connectionId - 1;
        if ((uint)index < (uint)clients.Count)
            clients[index].EnqueueData(payload, reliability);
    }

    private void DisposeClient(ClientEndpoint client)
    {
        int index = clients.IndexOf(client);
        if (index >= 0 && client.IsConnected) Disconnect(index + 1, ReadOnlySpan<byte>.Empty);
    }

    private void DisposeServer()
    {
        if (disposed) return;
        disposed = true;
        for (int i = 0; i < clients.Count; i++)
        {
            ClientEndpoint client = clients[i];
            if (client.IsConnected) client.DisconnectFromServer(Array.Empty<byte>(), supersede: false);
        }
    }

    /// <summary>
    /// Disposes the server endpoint and disconnects every live client once. Staged server-to-client data remains
    /// ahead of each client's terminal event. Further clients cannot be created.
    /// </summary>
    public void Dispose() => server.Dispose();

    private sealed class ServerEndpoint : INetTransport
    {
        private readonly InMemoryTransportHub hub;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<NetEvent> pending = new();
        private readonly HashSet<int> connected = new();
        private bool disposed;

        public ServerEndpoint(InMemoryTransportHub hub) => this.hub = hub;

        public void AddClient(int connectionId)
        {
            connected.Add(connectionId);
            pending.Add(NetEvent.Connected(new NetConnectionId(connectionId)));
        }

        public void EnqueueData(int connectionId, byte[] payload, NetChannelReliability reliability)
        {
            if (disposed || !connected.Contains(connectionId)) return;
            pending.Add(NetEvent.FromData(new NetConnectionId(connectionId), payload, reliability));
        }

        public void RemoveClient(int connectionId, byte[] reason, bool supersede)
        {
            if (!connected.Remove(connectionId)) return;
            var id = new NetConnectionId(connectionId);
            if (supersede)
                pending.RemoveAll(value => value.Connection == id);
            pending.Add(NetEvent.Disconnected(id, reason));
        }

        public void Poll()
        {
            if (disposed) return;
            for (int i = 0; i < pending.Count; i++) inbox.Enqueue(pending[i]);
            pending.Clear();
        }

        public bool TryDequeueEvent(out NetEvent value)
        {
            if (inbox.Count > 0)
            {
                value = inbox.Dequeue();
                return true;
            }
            value = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            if (disposed || !connected.Contains(target.Value)) return;
            hub.SendToClient(target.Value, payload.ToArray(), reliability);
        }

        public void Disconnect(NetConnectionId connection) =>
            hub.Disconnect(connection.Value, ReadOnlySpan<byte>.Empty);

        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) =>
            hub.Disconnect(connection.Value, reason);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            hub.DisposeServer();
            inbox.Clear();
            pending.Clear();
            connected.Clear();
        }
    }

    private sealed class ClientEndpoint : INetTransport
    {
        private static readonly NetConnectionId ServerId = new(1);
        private readonly InMemoryTransportHub hub;
        private readonly int connectionId;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<NetEvent> pending = new();
        private bool disconnected;
        private bool disposed;

        public ClientEndpoint(InMemoryTransportHub hub, int connectionId)
        {
            this.hub = hub;
            this.connectionId = connectionId;
            pending.Add(NetEvent.Connected(ServerId));
        }

        public bool IsConnected => !disconnected && !disposed;

        public void EnqueueData(byte[] payload, NetChannelReliability reliability)
        {
            if (IsConnected) pending.Add(NetEvent.FromData(ServerId, payload, reliability));
        }

        public void DisconnectFromServer(byte[] reason, bool supersede)
        {
            if (!IsConnected) return;
            disconnected = true;
            if (supersede) pending.Clear();
            pending.Add(NetEvent.Disconnected(ServerId, reason));
        }

        public void Poll()
        {
            if (disposed) return;
            for (int i = 0; i < pending.Count; i++) inbox.Enqueue(pending[i]);
            pending.Clear();
        }

        public bool TryDequeueEvent(out NetEvent value)
        {
            if (inbox.Count > 0)
            {
                value = inbox.Dequeue();
                return true;
            }
            value = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            if (!IsConnected) return;
            if (target != ServerId)
                throw new ArgumentException($"An in-memory hub client has only server id {ServerId.Value}.", nameof(target));
            hub.SendToServer(connectionId, payload.ToArray(), reliability);
        }

        public void Disconnect(NetConnectionId connection)
        {
            if (connection == ServerId) hub.Disconnect(connectionId, ReadOnlySpan<byte>.Empty);
        }

        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason)
        {
            if (connection == ServerId) hub.Disconnect(connectionId, reason);
        }

        public void Dispose()
        {
            if (disposed) return;
            hub.DisposeClient(this);
            disposed = true;
            disconnected = true;
            inbox.Clear();
            pending.Clear();
        }
    }
}
