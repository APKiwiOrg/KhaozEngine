using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Session server over an <see cref="INetTransport"/>: accepts connections, runs the Hello/Welcome handshake,
/// authenticates via <see cref="IConnectionAuthenticator"/>, assigns a player slot, and surfaces
/// Joined/Left/Data events (drain with <see cref="TryDequeueEvent"/> after <see cref="Poll"/>).
/// <para>The join gate also enforces ONE live session per authenticated subject
/// (<see cref="DuplicateSessionPolicy"/>): this is the single place every head goes through, and one account with two
/// live sessions is a shape nothing above this layer can represent (a persistence record is keyed by the account, so
/// the two sessions share one record and the last one to write wins). A tokenless connection has no subject and is
/// never deduped.</para>
/// </summary>
public sealed class NetServer
{
    private readonly INetTransport transport;
    private readonly IConnectionAuthenticator authenticator;
    private readonly SlotAllocator slots;
    private readonly Dictionary<int, NetConnectionId> connectionBySlot = new();
    private readonly Dictionary<NetConnectionId, int> slotByConnection = new();
    // The live session per authenticated subject, and its inverse for teardown. Only a NON-EMPTY subject is tracked:
    // a tokenless connection is anonymous, so there is nothing to be a duplicate OF. The two move together, so
    // subjectBySlot[n] is always the subject whose slotBySubject entry points back at n.
    private readonly Dictionary<string, int> slotBySubject = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> subjectBySlot = new();
    private readonly DuplicateSessionPolicy duplicateSessions;
    private readonly BoundedEventQueue<ServerSessionEvent> inbox;
    // Connections the transport has accepted that hold no slot yet: connected, Hello not yet answered. Until this
    // existed a Connected event was a no-op with nothing to count, so a connection flood was invisible here AND
    // unbounded, and the only cap in the stack (the per-connection rate limiter) does not engage until a slot exists.
    private readonly HashSet<NetConnectionId> pending = new();
    private readonly int maxPendingConnections;
    private long refusedPending;
    // One reusable buffer the per-tick game sends frame into, grown to the largest payload seen and then reused. A
    // broadcast frames ONCE into it and hands the same span to every peer, so the fan-out costs no allocation at all
    // rather than one frame plus a copy per player, every broadcast, every tick. Safe to reuse because Send is
    // synchronous and a transport may not retain the span past the call (see INetTransport.Send). Single-threaded by
    // the same contract that governs Poll, so there is no second caller to interleave with.
    private byte[] sendScratch = Array.Empty<byte>();

    /// <param name="transport">The byte transport to serve on (already listening).</param>
    /// <param name="maxPlayers">Slot capacity. A Hello past this is answered with a full-server refusal.</param>
    /// <param name="authenticator">Gate for incoming Hello tokens. Returns the verified subject on accept.</param>
    /// <param name="maxQueuedEvents">Defensive hard cap on undrained session events. The drain-to-empty contract
    /// (Poll then drain via <see cref="TryDequeueEvent"/> every tick) keeps this far below the cap; it only bites a
    /// host that stalls or is flooded, where the oldest event is dropped to keep memory bounded (Data events each
    /// pin a payload buffer). Left events are exempt: nothing re-announces a departure, so dropping one strands the
    /// host's per-player state. Drops are counted in <see cref="DroppedEventCount"/>.</param>
    /// <param name="duplicateSessions">What a Hello does when its authenticated subject already holds a slot. Default
    /// <see cref="DuplicateSessionPolicy.KickOlder"/>. Tokenless connections (empty subject) are never deduped.</param>
    /// <param name="maxPendingConnections">Global cap on connections that have been accepted but hold no slot yet
    /// (connected, Hello not yet answered). 0, the default, leaves it unlimited, which is the pre-cap behaviour. Above
    /// 0, a connect past the cap is refused immediately, so a connection flood degrades to refused handshakes instead
    /// of unbounded server-side state. Size it above the real concurrent-join burst a launch or a restart produces, not
    /// at <paramref name="maxPlayers"/>: a pending connection is cheap and a cap that bites normal traffic locks out
    /// legitimate players. Counted in <see cref="RefusedPendingConnectionCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPendingConnections"/> is negative.</exception>
    public NetServer(INetTransport transport, int maxPlayers, IConnectionAuthenticator authenticator,
        int maxQueuedEvents = BoundedEventQueue<ServerSessionEvent>.DefaultCapacity,
        DuplicateSessionPolicy duplicateSessions = DuplicateSessionPolicy.KickOlder,
        int maxPendingConnections = 0)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        if (maxPendingConnections < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingConnections), maxPendingConnections,
                "must be 0 (unlimited) or positive");
        slots = new SlotAllocator(maxPlayers);
        this.duplicateSessions = duplicateSessions;
        this.maxPendingConnections = maxPendingConnections;
        inbox = new BoundedEventQueue<ServerSessionEvent>(maxQueuedEvents);
    }

    /// <summary>Total session events dropped because the undrained inbox hit its cap. Non-zero means the host is
    /// not draining as contracted (a stall) or a peer is flooding; under normal operation this stays 0.</summary>
    public long DroppedEventCount => inbox.DroppedCount;

    /// <summary>Connections accepted but holding no slot yet: connected, Hello not yet answered. A handful at any
    /// instant is normal (that is a join in flight). A number that climbs and stays there is either a flood or a set
    /// of peers that connect and never say Hello.</summary>
    public int PendingConnectionCount => pending.Count;

    /// <summary>Total connects refused because <c>maxPendingConnections</c> was already reached. Stays 0 with no cap
    /// configured, and 0 under normal traffic with one. Non-zero means the cap engaged, which is either a flood being
    /// shed or a cap set below this server's real join burst.</summary>
    public long RefusedPendingConnectionCount => refusedPending;

    /// <summary>Pumps the transport and processes handshake/data/disconnect into session events.</summary>
    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    // Pending: no slot until a valid Hello arrives, so the only server-side state this connection
                    // pins until then is its entry here. Cap the set and a connection flood degrades to refused
                    // handshakes. The refusal is a BARE disconnect, not the framed Reject a rejected Hello gets: a
                    // cap whose job is to shed a flood must not answer every flooded connect with bytes of its own.
                    // A legitimate client refused this way reads a plain drop and comes back on its backoff, which is
                    // the right answer for a transient capacity limit.
                    if (maxPendingConnections > 0 && pending.Count >= maxPendingConnections)
                    {
                        refusedPending++;
                        transport.Disconnect(ev.Connection);
                        break;
                    }
                    pending.Add(ev.Connection);
                    break;
                case NetEventType.Disconnected:
                    pending.Remove(ev.Connection);
                    if (slotByConnection.TryGetValue(ev.Connection, out int leftSlot))
                    {
                        RemovePeer(ev.Connection, leftSlot);
                        // Terminal: the host frees its own per-player state (save-on-leave, despawn) off this and
                        // off nothing else, so an overflow that dropped it would strand that state the way a
                        // dropped transport Disconnected used to strand the slot itself.
                        inbox.EnqueueTerminal(ServerSessionEvent.Left(leftSlot));
                    }
                    break;
                case NetEventType.Data:
                    HandleData(ev);
                    break;
            }
        }
    }

    private void HandleData(NetEvent ev)
    {
        SessionOpcode op = SessionFrame.ReadOpcode(ev.Data);
        if (slotByConnection.TryGetValue(ev.Connection, out int slot))
        {
            // Established peer: only Data is meaningful; ignore stray control opcodes.
            if (op == SessionOpcode.Data)
                inbox.Enqueue(ServerSessionEvent.FromData(slot, SessionFrame.ReadBody(ev.Data), ev.Reliability));
            return;
        }

        // Not yet established: the only thing we accept is a Hello.
        if (op != SessionOpcode.Hello) return;

        byte[] token = SessionFrame.ReadBody(ev.Data);
        if (!authenticator.TryAuthenticate(token, out string subject, out string reason))
        {
            RejectAndDisconnect(ev.Connection, reason);
            return;
        }
        // One live session per account. A tokenless connection carries no subject, so it is never a duplicate of
        // anything and two guests are two people, not one account twice. Null is read as the same "no subject" the
        // empty string is: the out parameter is non-nullable, but a third-party authenticator compiled without
        // nullable reference types can hand back null on an ACCEPT, and dereferencing it here would take the server
        // down on a connection it used to admit.
        if (!string.IsNullOrEmpty(subject) && slotBySubject.TryGetValue(subject, out int heldSlot))
        {
            if (duplicateSessions == DuplicateSessionPolicy.RefuseNewer)
            {
                RejectAndDisconnect(ev.Connection, SessionRejectReason.AlreadySignedIn);
                return;
            }
            EndOlderSession(heldSlot);
        }
        if (!slots.TryAllocate(out int newSlot))
        {
            RejectAndDisconnect(ev.Connection, "server full");
            return;
        }
        connectionBySlot[newSlot] = ev.Connection;
        slotByConnection[ev.Connection] = newSlot;
        pending.Remove(ev.Connection);   // it holds a slot now, so it is no longer what the pending cap governs
        if (!string.IsNullOrEmpty(subject)) { slotBySubject[subject] = newSlot; subjectBySlot[newSlot] = subject; }
        // Little-endian by the wire format SessionOpcode.Welcome documents, not by whatever the host happens to be.
        // BitConverter writes in the running process's native order, which agrees with the documented format on every
        // machine the fleet runs on and silently disagrees anywhere else, including in an independently written
        // decoder that trusted the doc comment.
        var slotBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(slotBytes, newSlot);
        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Welcome, slotBytes), NetChannelReliability.ReliableOrdered);
        // Surface a verified display name from the token when the authenticator can provide one (opt-in seam).
        string displayName = authenticator is IConnectionDisplayName named ? named.ReadDisplayName(token) : string.Empty;
        inbox.Enqueue(ServerSessionEvent.Joined(newSlot, token, subject, displayName));
    }

    // Ends a subject's older session so its successor can take the seat, BEFORE the successor is admitted. The Left
    // goes into the same inbox the new Joined is about to go into, so a host draining in order (both heads do) runs
    // the old session's OnLeave - and therefore its save-on-leave - ahead of the new session's OnJoin and its
    // load-on-join, and never sees the two overlap. Leaving it to the transport's own Disconnected event instead
    // would put the Joined first and reopen the two-live-sessions window this gate exists to close. Releasing the
    // slot here also means the successor usually recycles it, which is the same seat-reuse a leave/rejoin produces.
    private void EndOlderSession(int heldSlot)
    {
        if (!connectionBySlot.TryGetValue(heldSlot, out NetConnectionId held)) return;
        RejectAndDisconnect(held, SessionRejectReason.SignedInElsewhere);
        RemovePeer(held, heldSlot);
        inbox.EnqueueTerminal(ServerSessionEvent.Left(heldSlot));
    }

    // Refuse a pending peer: send the reliable Reject (delivered as-is over a lossless transport such as the
    // loopback) AND carry the SAME framed Reject on the disconnect itself, so the reason still reaches the client
    // when the immediate teardown outruns the reliable flush (the real-UDP path). Without the reason on the
    // disconnect the client saw only a bare transport drop, read it as a transient outage, and auto-reconnected
    // forever instead of surfacing the terminal rejection - the "reconnect never succeeds after a deploy" bug.
    private void RejectAndDisconnect(NetConnectionId connection, string reason)
    {
        // Torn down here, so it pins nothing further. Not left to the transport's own Disconnected event: a transport
        // that tells only the PEER about a disconnect it was asked to make (the in-memory loopback is one) would never
        // surface one for this connection, and the entry would sit in `pending` for the process's lifetime, which is
        // the same slow leak the cap exists to prevent.
        pending.Remove(connection);
        byte[] frame = SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes(reason));
        transport.Send(connection, frame, NetChannelReliability.ReliableOrdered);
        transport.Disconnect(connection, frame);
    }

    private void RemovePeer(NetConnectionId conn, int slot)
    {
        pending.Remove(conn);
        slotByConnection.Remove(conn);
        connectionBySlot.Remove(slot);
        if (subjectBySlot.Remove(slot, out string? subject)) slotBySubject.Remove(subject);
        slots.Release(slot);
    }

    /// <summary>Drains one session event. False when none remain this poll.</summary>
    public bool TryDequeueEvent(out ServerSessionEvent ev) => inbox.TryDequeue(out ev);

    /// <summary>Sends game data to one slot. No-op for an unknown slot.</summary>
    public void SendTo(int slot, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (!connectionBySlot.TryGetValue(slot, out NetConnectionId conn)) return;
        transport.Send(conn, FrameForSend(payload), reliability);
    }

    /// <summary>Disconnects one slot's connection (a kick). The transport surfaces the resulting Disconnected event
    /// on a later poll, which frees the slot (and a recycling join may reuse it). No-op for an unknown slot.</summary>
    public void Disconnect(int slot)
    {
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            transport.Disconnect(conn);
    }

    /// <summary>Disconnects one slot's connection (a kick) CARRYING <paramref name="reason"/>, so the kicked client
    /// learns why rather than seeing a bare drop it would read as a transient outage and reconnect on. The reason
    /// rides the same two paths a refused Hello uses (a reliable Reject frame AND the disconnect itself), so it
    /// survives a teardown that outruns the reliable flush over real UDP. Surfaces on <c>WorldClient</c> as
    /// <c>DisconnectReasonDetail</c>.
    /// <para>A STABLE TOKEN, not display text: a headless server owns no string catalog, so send something the
    /// client matches and renders from its own localization (the shape <see cref="SessionRejectReason"/> uses).</para>
    /// No-op for an unknown slot.</summary>
    public void Disconnect(int slot, string reason)
    {
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            RejectAndDisconnect(conn, reason ?? string.Empty);
    }

    /// <summary>Sends game data to every joined slot. The frame is built once and the same bytes go to every peer.</summary>
    public void Broadcast(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        ReadOnlySpan<byte> frame = FrameForSend(payload);
        foreach (NetConnectionId conn in connectionBySlot.Values)
            transport.Send(conn, frame, reliability);
    }

    // Frames a game payload into the reusable send buffer and hands back the written window. The buffer only ever
    // grows, so a steady tick rate settles on one allocation for the whole server rather than one per send.
    private ReadOnlySpan<byte> FrameForSend(ReadOnlySpan<byte> payload)
    {
        int length = SessionFrame.FrameLength(payload.Length);
        if (sendScratch.Length < length) sendScratch = new byte[length];
        SessionFrame.Write(SessionOpcode.Data, payload, sendScratch);
        return sendScratch.AsSpan(0, length);
    }
}
