using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>
/// Server-side <see cref="INetTransport"/> over LiteNetLib reliable-UDP. Listens on a UDP port, accepts
/// connections whose key matches <c>connectionKey</c>, and surfaces each peer as
/// <see cref="NetConnectionId"/> = <c>peer.Id + 1</c> (so a valid id is always positive). Reuses
/// <see cref="ChannelSplitter.ToDeliveryMethod"/> for the reliability mapping. Single-threaded: call
/// <see cref="Poll"/> from the host-loop thread.
/// </summary>
public sealed class LiteNetLibServerTransport : INetTransport
{
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager manager;
    private readonly Queue<NetEvent> inbox = new();
    private readonly Dictionary<int, NetPeer> peersById = new();
    private readonly string connectionKey;

    /// <param name="port">UDP port to listen on; 0 lets the OS assign a free port (useful in tests).</param>
    /// <param name="connectionKey">Shared key a client must present to be accepted.</param>
    public LiteNetLibServerTransport(int port, string connectionKey = "khaoz")
    {
        this.connectionKey = connectionKey ?? throw new ArgumentNullException(nameof(connectionKey));
        manager = new NetManager(listener);
        WireListener();
        if (!manager.Start(port))
            throw new InvalidOperationException($"Failed to start UDP listener on port {port}.");
    }

    private static NetConnectionId ToId(NetPeer peer) => new(peer.Id + 1);

    private void WireListener()
    {
        listener.ConnectionRequestEvent += request => request.AcceptIfKey(connectionKey);

        listener.PeerConnectedEvent += peer =>
        {
            peersById[peer.Id] = peer;
            inbox.Enqueue(NetEvent.Connected(ToId(peer)));
        };

        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            peersById.Remove(peer.Id);
            inbox.Enqueue(NetEvent.Disconnected(ToId(peer)));
        };

        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            byte[] data = reader.GetRemainingBytes();
            NetChannelReliability reliability = deliveryMethod == DeliveryMethod.ReliableOrdered
                ? NetChannelReliability.ReliableOrdered
                : NetChannelReliability.UnreliableSequenced;
            inbox.Enqueue(NetEvent.FromData(ToId(peer), data, reliability));
            reader.Recycle();
        };
    }

    public void Poll() => manager.PollEvents();

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (peersById.TryGetValue(target.Value - 1, out NetPeer? peer))
            peer.Send(payload.ToArray(), ChannelSplitter.ToDeliveryMethod(reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (peersById.TryGetValue(connection.Value - 1, out NetPeer? peer))
            peer.Disconnect();
    }

    public void Dispose() => manager.Stop();
}
