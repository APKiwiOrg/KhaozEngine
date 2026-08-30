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
    private readonly BoundedEventQueue<NetEvent> inbox;
    private readonly Dictionary<int, NetPeer> peersById = new();
    private readonly string connectionKey;

    /// <param name="port">UDP port to listen on; 0 lets the OS assign a free port (useful in tests).</param>
    /// <param name="connectionKey">Shared key a client must present to be accepted.</param>
    /// <param name="maxQueuedEvents">Defensive hard cap on undrained transport events. Under the drain-each-poll
    /// contract this never bites; a stalled or flooded host drops the oldest event (each Data event holds a fresh
    /// payload buffer) instead of growing memory without bound. Disconnected events are exempt (they release a
    /// player slot, so dropping one leaks it). Drops are counted in <see cref="DroppedEventCount"/>.</param>
    public LiteNetLibServerTransport(int port, string connectionKey = "khaoz",
        int maxQueuedEvents = BoundedEventQueue<NetEvent>.DefaultCapacity)
    {
        this.connectionKey = connectionKey ?? throw new ArgumentNullException(nameof(connectionKey));
        inbox = new BoundedEventQueue<NetEvent>(maxQueuedEvents);
        manager = new NetManager(listener);
        WireListener();
        if (!manager.Start(port))
            throw new InvalidOperationException($"Failed to start UDP listener on port {port}.");
    }

    /// <summary>Total transport events dropped because the undrained inbox hit its cap; 0 under normal draining.</summary>
    public long DroppedEventCount => inbox.DroppedCount;

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
            // Terminal: the session layer releases the peer's player slot off THIS event and off nothing else, so
            // an overflow that dropped it would leak that slot for the life of the process. Exempt from the cap.
            inbox.EnqueueTerminal(NetEvent.Disconnected(ToId(peer)));
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

    public bool TryDequeueEvent(out NetEvent ev) => inbox.TryDequeue(out ev);

    /// <summary>Sends straight from the caller's span. LiteNetLib's span overload copies into its own packet before
    /// returning, so nothing here retains the borrowed bytes, and a broadcast no longer pays one array copy per peer
    /// for the identical frame.</summary>
    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (peersById.TryGetValue(target.Value - 1, out NetPeer? peer))
            peer.Send(payload, ChannelSplitter.ToDeliveryMethod(reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (peersById.TryGetValue(connection.Value - 1, out NetPeer? peer))
            peer.Disconnect();
    }

    /// <summary>Disconnects the peer, riding <paramref name="reason"/> on LiteNetLib's disconnect handshake (the
    /// remote reads it back as the disconnect's additional data). This is how a server reject reaches the client
    /// reliably: a separately-sent reliable Reject can be lost when the teardown outruns its flush, but the
    /// disconnect payload is part of the shutdown itself.</summary>
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason)
    {
        if (!peersById.TryGetValue(connection.Value - 1, out NetPeer? peer)) return;
        if (reason.IsEmpty) peer.Disconnect();
        else peer.Disconnect(reason.ToArray());
    }

    public void Dispose() => manager.Stop();
}
