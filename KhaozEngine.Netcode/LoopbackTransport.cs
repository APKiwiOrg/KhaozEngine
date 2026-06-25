using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// A deterministic, in-memory transport pair: no sockets, no threads. <see cref="CreatePair"/> returns two
/// linked endpoints; a Send on one becomes a Data event on the other after that other endpoint Polls. Both
/// endpoints observe the peer as connection id 1, and each surfaces a Connected event for the peer on its
/// first Poll. Used for headless netcode tests and single-process local play.
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
    private static readonly NetConnectionId PeerId = new(1);

    private readonly Queue<NetEvent> inbox = new();
    private readonly List<(byte[] data, NetChannelReliability reliability)> pendingFromPeer = new();
    private LoopbackTransport? peer;
    private bool announcedConnect;
    private bool disposed;

    private LoopbackTransport() { }

    /// <summary>Creates two linked endpoints (e.g. a server end and a client end).</summary>
    public static (LoopbackTransport a, LoopbackTransport b) CreatePair()
    {
        var a = new LoopbackTransport();
        var b = new LoopbackTransport();
        a.peer = b;
        b.peer = a;
        return (a, b);
    }

    public void Poll()
    {
        if (disposed) return;

        if (!announcedConnect && peer is not null)
        {
            announcedConnect = true;
            inbox.Enqueue(NetEvent.Connected(PeerId));
        }

        // Surface anything the peer sent us, in send order (deterministic).
        for (int i = 0; i < pendingFromPeer.Count; i++)
        {
            (byte[] data, NetChannelReliability reliability) = pendingFromPeer[i];
            inbox.Enqueue(NetEvent.FromData(PeerId, data, reliability));
        }
        pendingFromPeer.Clear();
    }

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (disposed || peer is null) return;
        if (target != PeerId)
            throw new ArgumentException($"Loopback has only peer id {PeerId.Value}.", nameof(target));
        peer.pendingFromPeer.Add((payload.ToArray(), reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (disposed || peer is null) return;
        peer.inbox.Enqueue(NetEvent.Disconnected(PeerId));
        peer.peer = null;
        peer = null;
    }

    public void Dispose()
    {
        disposed = true;
        peer = null;
        inbox.Clear();
        pendingFromPeer.Clear();
    }
}
