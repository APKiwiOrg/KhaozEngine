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

    /// <summary>Sends <paramref name="payload"/> to <paramref name="target"/> on the given reliability channel.
    /// <para><paramref name="payload"/> is BORROWED for the duration of the call and no longer: an implementation that
    /// needs the bytes afterwards copies them (the loopback stages a copy, the UDP binding hands them to LiteNetLib,
    /// which copies into its own packet before returning). That is what lets a caller frame once into a buffer it
    /// keeps and hand the same span to every peer of a fan-out, which is what <see cref="NetServer.Broadcast"/>
    /// does.</para></summary>
    void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability);

    /// <summary>Disconnects a single connection. No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection);

    /// <summary>Disconnects a single connection, carrying <paramref name="reason"/> in the disconnect itself so a
    /// rejecting server conveys WHY even when a separately-sent reliable frame would be lost to the teardown. Both
    /// in-tree transports implement it: the UDP binding rides it on LiteNetLib's disconnect handshake, and the
    /// in-memory loopback delivers it as the single Disconnected that ends the session, superseding what the peer has
    /// not yet polled, so a rejected client sees one terminal event either way. The default drops the reason and does
    /// a plain disconnect, which is all an external transport with nowhere to put one can do, and it leaves such a
    /// transport compiling unchanged. No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => Disconnect(connection);

    /// <summary>
    /// Live connection statistics (RTT, packet loss, cumulative byte counters). Optional: the default returns
    /// <see cref="NetTransportStats.Unavailable"/>, so existing transports (e.g. the in-memory loopback) need not
    /// implement it; the LiteNetLib UDP binding overrides it. Surfaced to games via <c>WorldClient.NetStats</c>
    /// (wrapped in a <c>ClientNetStats</c> alongside that client's own rate window) and <c>TileWorldClient.NetStats</c>
    /// (forwarded as it stands).
    /// </summary>
    NetTransportStats Stats => NetTransportStats.Unavailable;
}
