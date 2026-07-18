using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A pass-through <see cref="INetTransport"/> decorator that records the <see cref="NetChannelReliability"/> of every
/// outbound <see cref="Send"/>. Used to prove the game-message send path forwards the caller's chosen channel to the
/// transport verbatim (rather than hardcoding one), which a loopback round-trip alone can't catch since loopback
/// delivers both channels identically.
/// </summary>
internal sealed class RecordingTransport : INetTransport
{
    private readonly INetTransport inner;
    public List<(byte[] payload, NetChannelReliability reliability)> Sends { get; } = new();

    public RecordingTransport(INetTransport inner) => this.inner = inner;

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        Sends.Add((payload.ToArray(), reliability));
        inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Dispose() => inner.Dispose();
}
