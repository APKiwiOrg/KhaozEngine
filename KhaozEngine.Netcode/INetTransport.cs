using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// The byte-transport seam: the only thing the netcode stack knows about the wire. Implementations are an
/// in-memory loopback (deterministic; headless tests + local play) or a real UDP binding
/// (KhaozEngine.Netcode.LiteNetLib). Server vs client role is decided at construction; this interface is
/// pure I/O. Single-threaded by contract: call <see cref="Poll"/> then drain with <see cref="TryDequeueEvent"/>
/// from the same thread that owns the host loop.
/// </summary>
public interface INetTransport : IDisposable
{
    /// <summary>Pumps the underlying transport, enqueueing any pending events for <see cref="TryDequeueEvent"/>.</summary>
    void Poll();

    /// <summary>Drains one queued event in arrival order. Returns false when none remain this poll.</summary>
    bool TryDequeueEvent(out NetEvent ev);

    /// <summary>Sends <paramref name="payload"/> to <paramref name="target"/> on the given reliability channel.</summary>
    void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability);

    /// <summary>Disconnects a single connection. No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection);

    /// <summary>Disconnects a single connection, carrying <paramref name="reason"/> in the disconnect itself so a
    /// rejecting server conveys WHY even when a separately-sent reliable frame would be lost to the teardown. The
    /// UDP binding rides it on LiteNetLib's disconnect handshake and the peer surfaces it on the resulting
    /// <see cref="NetEvent"/> Disconnected payload. The default drops the reason and does a plain disconnect, so a
    /// lossless transport (the in-memory loopback, which delivers the reliable Reject anyway) needs no change.
    /// No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => Disconnect(connection);

    /// <summary>
    /// Live connection statistics (RTT, packet loss, cumulative byte counters). Optional: the default returns
    /// <see cref="NetTransportStats.Unavailable"/>, so existing transports (e.g. the in-memory loopback) need not
    /// implement it; the LiteNetLib UDP binding overrides it. Surfaced to games via <c>WorldClient.NetStats</c>.
    /// </summary>
    NetTransportStats Stats => NetTransportStats.Unavailable;
}
