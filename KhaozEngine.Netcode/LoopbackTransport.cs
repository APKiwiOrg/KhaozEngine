using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// A deterministic, in-memory transport pair: no sockets, no threads. <see cref="CreatePair"/> returns two
/// linked endpoints; a Send on one becomes a Data event on the other after that other endpoint Polls. Both
/// endpoints observe the peer as connection id 1, and each surfaces a Connected event for the peer on its
/// first Poll. Used for headless netcode tests and single-process local play.
/// <para>Everything one endpoint hands the other, data and the disconnect alike, goes through ONE staging list in
/// call order and is promoted into the peer's queue on the peer's next Poll. That ordering is the whole contract:
/// a disconnect enqueued straight into the peer's queue would jump ahead of frames sent before it, which is how a
/// rejected client used to observe a bare drop before the Reject that explains it.</para>
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
    private static readonly NetConnectionId PeerId = new(1);

    private readonly Queue<NetEvent> inbox = new();
    // What this endpoint has been handed but not yet polled, in the order the peer produced it. Holding whole
    // NetEvents rather than payloads is what lets a disconnect take its true place in that order.
    private readonly List<NetEvent> pendingFromPeer = new();
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

        // Surface everything the peer handed us, in the order it did (deterministic). Deliberately independent of
        // whether the link is still up: these events are already delivered as far as the peer is concerned, and the
        // disconnect that tore the link down is itself one of them.
        for (int i = 0; i < pendingFromPeer.Count; i++) inbox.Enqueue(pendingFromPeer[i]);
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
        peer.pendingFromPeer.Add(NetEvent.FromData(PeerId, payload.ToArray(), reliability));
    }

    /// <summary>Drops the link, after everything already sent on it. A plain kick loses nothing: whatever the peer
    /// was handed before this call still surfaces on its next Poll, then the Disconnected.</summary>
    public void Disconnect(NetConnectionId connection)
    {
        if (disposed || peer is null) return;
        peer.pendingFromPeer.Add(NetEvent.Disconnected(PeerId));
        Unlink();
    }

    /// <summary>
    /// Drops the link carrying <paramref name="reason"/> on the disconnect itself, and SUPERSEDES whatever the peer
    /// has not yet polled. That last part is the point, and it mirrors the UDP binding rather than diverging from it.
    /// <para><see cref="NetServer"/> refuses a peer by sending the framed Reject reliably AND riding the same frame on
    /// the disconnect, because over a real socket exactly one of the two arrives: LiteNetLib's teardown outruns the
    /// unflushed reliable frame, so the client sees a single Disconnected whose payload is the Reject. A lossless
    /// loopback that delivered both would hand the client the same terminal rejection twice, and delivering the
    /// reasonless drop first (what the reason-dropping default interface method used to do here) was worse still: a
    /// consumer that auto-reconnects on a bare Disconnected fired before the Rejected explaining it was even drained.
    /// So a reason-carrying disconnect ends the session with one event, the way the wire does.</para>
    /// <para>An EMPTY reason is not a refusal and behaves like <see cref="Disconnect(NetConnectionId)"/>, matching the
    /// UDP binding's own empty-reason branch.</para>
    /// </summary>
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason)
    {
        if (disposed || peer is null) return;
        if (reason.IsEmpty) { Disconnect(connection); return; }
        peer.pendingFromPeer.Clear();
        peer.pendingFromPeer.Add(NetEvent.Disconnected(PeerId, reason.ToArray()));
        Unlink();
    }

    // Breaks the link both ways. Runs AFTER the disconnect event has been staged, so the staging above can never be
    // aimed at an endpoint this call has already let go of.
    private void Unlink()
    {
        if (peer is null) return;
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
