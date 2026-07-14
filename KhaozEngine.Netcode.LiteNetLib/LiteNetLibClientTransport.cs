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
    private readonly BoundedEventQueue<NetEvent> inbox;
    private readonly Dictionary<int, NetPeer> peersById = new();

    /// <param name="host">Server host/IP to connect to.</param>
    /// <param name="port">Server UDP port.</param>
    /// <param name="connectionKey">Shared key presented to the server; must match the server's key.</param>
    /// <param name="maxQueuedEvents">Defensive hard cap on undrained transport events. Under the drain-each-poll
    /// contract this never bites; a stalled or flooded host drops the oldest event (each Data event holds a fresh
    /// payload buffer) instead of growing memory without bound. Drops are counted in <see cref="DroppedEventCount"/>.</param>
    public LiteNetLibClientTransport(string host, int port, string connectionKey = "khaoz",
        int maxQueuedEvents = BoundedEventQueue<NetEvent>.DefaultCapacity)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (connectionKey is null) throw new ArgumentNullException(nameof(connectionKey));
        inbox = new BoundedEventQueue<NetEvent>(maxQueuedEvents);
        manager = new NetManager(listener);
        manager.EnableStatistics = true; // cheap; lets Stats report per-peer bytes/loss to the diagnostics overlay
        WireListener();
        if (!manager.Start())
            throw new InvalidOperationException("Failed to start client transport.");
        manager.Connect(host, port, connectionKey);
    }

    /// <summary>Total transport events dropped because the undrained inbox hit its cap; 0 under normal draining.</summary>
    public long DroppedEventCount => inbox.DroppedCount;

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
            // A rejecting server rides the framed Reject on the disconnect itself (see LiteNetLibServerTransport);
            // surface it as the Disconnected payload so NetClient can read the terminal reason even when the
            // separately-sent reliable Reject was lost to the teardown.
            byte[]? reason = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetRemainingBytes() : null;
            inbox.Enqueue(NetEvent.Disconnected(ToId(peer), reason));
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

    /// <summary>
    /// Connection stats for the server peer (the client holds a single peer). RTT comes from LiteNetLib's RTT
    /// estimator; loss and byte counters from per-peer <see cref="NetStatistics"/> (populated because
    /// <c>EnableStatistics</c> is on). <see cref="NetTransportStats.Unavailable"/> until the peer connects.
    /// </summary>
    public NetTransportStats Stats
    {
        get
        {
            foreach (NetPeer peer in peersById.Values)
            {
                NetStatistics st = peer.Statistics;
                float loss = st is null ? 0f : Math.Clamp(st.PacketLossPercent / 100f, 0f, 1f);
                return new NetTransportStats(
                    connected: true,
                    rttMs: peer.RoundTripTime,
                    packetLoss: loss,
                    bytesReceivedTotal: st?.BytesReceived ?? 0L,
                    bytesSentTotal: st?.BytesSent ?? 0L);
            }
            return NetTransportStats.Unavailable;
        }
    }

    public void Dispose() => manager.Stop();
}
