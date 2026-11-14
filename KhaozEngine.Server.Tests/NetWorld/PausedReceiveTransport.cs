using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>Holds incoming events on a real transport while its peer and disconnect path keep running.</summary>
internal sealed class PausedReceiveTransport(INetTransport inner) : INetTransport
{
    private bool disposed;

    public bool Paused { get; set; }

    public void Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        inner.Poll();
    }

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (!Paused) return inner.TryDequeueEvent(out ev);
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
        inner.Send(target, payload, reliability);

    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);

    public void Dispose()
    {
        disposed = true;
        inner.Dispose();
    }
}
