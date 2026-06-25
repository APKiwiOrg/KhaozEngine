using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>
/// Client-side <see cref="INetTransport"/> over LiteNetLib reliable-UDP. Connects to a server on
/// construction and surfaces the server peer as <see cref="NetConnectionId"/> = <c>peer.Id + 1</c>. Reuses
/// <see cref="ChannelSplitter.ToDeliveryMethod"/> for the reliability mapping.
/// </summary>
public sealed class LiteNetLibClientTransport : INetTransport
{
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager manager;
    private readonly Queue<NetEvent> inbox = new();
    private readonly Dictionary<int, NetPeer> peersById = new();

    /// <param name="host">Server host/IP to connect to.</param>
    /// <param name="port">Server UDP port.</param>
    /// <param name="connectionKey">Shared key presented to the server; must match the server's key.</param>
    public LiteNetLibClientTransport(string host, int port, string connectionKey = "khaoz")
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (connectionKey is null) throw new ArgumentNullException(nameof(connectionKey));
        manager = new NetManager(listener);
        WireListener();
        if (!manager.Start())
            throw new InvalidOperationException("Failed to start client transport.");
        manager.Connect(host, port, connectionKey);
    }

    private static NetConnectionId ToId(NetPeer peer) => new(peer.Id + 1);

    private void WireListener()
    {
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
