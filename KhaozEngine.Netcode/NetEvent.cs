using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// A single transport event drained via <see cref="INetTransport.TryDequeueEvent"/>. For
/// <see cref="NetEventType.Data"/> the payload is in <see cref="Data"/> and the channel it arrived on is
/// <see cref="Reliability"/>. A <see cref="NetEventType.Disconnected"/> may carry an optional reason payload in
/// <see cref="Data"/> (a rejecting server rides the framed Reject on the disconnect itself); a Connected event
/// and an ordinary drop carry an empty payload.
/// </summary>
/// <remarks>
/// Phase 0 keeps <see cref="Data"/> as an owned <c>byte[]</c> copy for simplicity. A later phase replaces it
/// with pooled buffers to cut per-event allocation; consumers should treat the array as read-only and not retain it.
/// </remarks>
public readonly struct NetEvent
{
    public NetEvent(NetEventType type, NetConnectionId connection, byte[] data, NetChannelReliability reliability)
    {
        Type = type;
        Connection = connection;
        Data = data ?? Array.Empty<byte>();
        Reliability = reliability;
    }

    /// <summary>The kind of event.</summary>
    public NetEventType Type { get; }

    /// <summary>The connection the event concerns.</summary>
    public NetConnectionId Connection { get; }

    /// <summary>The payload for a <see cref="NetEventType.Data"/> event, or the optional reason a
    /// <see cref="NetEventType.Disconnected"/> event carries; empty otherwise.</summary>
    public byte[] Data { get; }

    /// <summary>The channel a <see cref="NetEventType.Data"/> event arrived on.</summary>
    public NetChannelReliability Reliability { get; }

    /// <summary>A connect event for <paramref name="c"/>.</summary>
    public static NetEvent Connected(NetConnectionId c) =>
        new(NetEventType.Connected, c, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    /// <summary>A disconnect event for <paramref name="c"/>, optionally carrying <paramref name="data"/> as the
    /// reason (a server that rejects a Hello disconnects WITH the framed Reject, so the reason survives the
    /// teardown even when a separately-sent reliable Reject frame is lost to it). Empty for an ordinary drop.</summary>
    public static NetEvent Disconnected(NetConnectionId c, byte[]? data = null) =>
        new(NetEventType.Disconnected, c, data ?? Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    /// <summary>A data event carrying <paramref name="data"/> received on <paramref name="reliability"/>.</summary>
    public static NetEvent FromData(NetConnectionId c, byte[] data, NetChannelReliability reliability) =>
        new(NetEventType.Data, c, data, reliability);
}
