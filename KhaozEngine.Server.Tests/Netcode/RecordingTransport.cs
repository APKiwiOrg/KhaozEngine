using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// An <see cref="INetTransport"/> double that delivers exactly the events a test stages and records every Send and
/// Disconnect the subject makes. Unlike <see cref="LoopbackTransport"/> it puts no constraint on connection ids, so
/// a test can hand the subject a peer numbered something other than 1 and see which id the subject aims at.
/// </summary>
internal sealed class RecordingTransport : INetTransport
{
    private readonly Queue<NetEvent> inbound = new();

    /// <summary>Every Send, in order: the target, a copy of the payload, and the channel it went out on.</summary>
    public List<(NetConnectionId Target, byte[] Payload, NetChannelReliability Reliability)> Sent { get; } = new();

    /// <summary>Every Disconnect, in order: the target and a copy of the reason (null for the reasonless form).</summary>
    public List<(NetConnectionId Target, byte[]? Reason)> Disconnects { get; } = new();

    /// <summary>Stages an event for the subject's next drain.</summary>
    public void Deliver(NetEvent ev) => inbound.Enqueue(ev);

    public void Poll() { }

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbound.Count > 0) { ev = inbound.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
        Sent.Add((target, payload.ToArray(), reliability));

    public void Disconnect(NetConnectionId connection) => Disconnects.Add((connection, null));

    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) =>
        Disconnects.Add((connection, reason.ToArray()));

    public void Dispose() { }
}
