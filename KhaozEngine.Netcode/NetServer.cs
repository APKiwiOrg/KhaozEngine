using System;
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

    /// <param name="transport">The byte transport to serve on (already listening).</param>
    /// <param name="maxPlayers">Slot capacity. A Hello past this is answered with a full-server refusal.</param>
    /// <param name="authenticator">Gate for incoming Hello tokens. Returns the verified subject on accept.</param>
    /// <param name="maxQueuedEvents">Defensive hard cap on undrained session events. The drain-to-empty contract
    /// (Poll then drain via <see cref="TryDequeueEvent"/> every tick) keeps this far below the cap; it only bites a
    /// host that stalls or is flooded, where the oldest event is dropped to keep memory bounded (Data events each
    /// pin a payload buffer). Drops are counted in <see cref="DroppedEventCount"/>.</param>
    /// <param name="duplicateSessions">What a Hello does when its authenticated subject already holds a slot. Default
    /// <see cref="DuplicateSessionPolicy.KickOlder"/>. Tokenless connections (empty subject) are never deduped.</param>
    public NetServer(INetTransport transport, int maxPlayers, IConnectionAuthenticator authenticator,
        int maxQueuedEvents = BoundedEventQueue<ServerSessionEvent>.DefaultCapacity,
        DuplicateSessionPolicy duplicateSessions = DuplicateSessionPolicy.KickOlder)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        slots = new SlotAllocator(maxPlayers);
        this.duplicateSessions = duplicateSessions;
        inbox = new BoundedEventQueue<ServerSessionEvent>(maxQueuedEvents);
    }

    /// <summary>Total session events dropped because the undrained inbox hit its cap. Non-zero means the host is
    /// not draining as contracted (a stall) or a peer is flooding; under normal operation this stays 0.</summary>
    public long DroppedEventCount => inbox.DroppedCount;

    /// <summary>Pumps the transport and processes handshake/data/disconnect into session events.</summary>
    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    // Pending: no slot until a valid Hello arrives.
                    break;
                case NetEventType.Disconnected:
                    if (slotByConnection.TryGetValue(ev.Connection, out int leftSlot))
                    {
                        RemovePeer(ev.Connection, leftSlot);
                        inbox.Enqueue(ServerSessionEvent.Left(leftSlot));
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
        if (!string.IsNullOrEmpty(subject)) { slotBySubject[subject] = newSlot; subjectBySlot[newSlot] = subject; }
        var slotBytes = new byte[4];
        BitConverter.TryWriteBytes(slotBytes, newSlot);
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
        inbox.Enqueue(ServerSessionEvent.Left(heldSlot));
    }

    // Refuse a pending peer: send the reliable Reject (delivered as-is over a lossless transport such as the
    // loopback) AND carry the SAME framed Reject on the disconnect itself, so the reason still reaches the client
    // when the immediate teardown outruns the reliable flush (the real-UDP path). Without the reason on the
    // disconnect the client saw only a bare transport drop, read it as a transient outage, and auto-reconnected
    // forever instead of surfacing the terminal rejection - the "reconnect never succeeds after a deploy" bug.
    private void RejectAndDisconnect(NetConnectionId connection, string reason)
    {
        byte[] frame = SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes(reason));
        transport.Send(connection, frame, NetChannelReliability.ReliableOrdered);
        transport.Disconnect(connection, frame);
    }

    private void RemovePeer(NetConnectionId conn, int slot)
    {
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
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            transport.Send(conn, SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }

    /// <summary>Disconnects one slot's connection (a kick). The transport surfaces the resulting Disconnected event
    /// on a later poll, which frees the slot (and a recycling join may reuse it). No-op for an unknown slot.</summary>
    public void Disconnect(int slot)
    {
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            transport.Disconnect(conn);
    }

    /// <summary>Sends game data to every joined slot.</summary>
    public void Broadcast(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] frame = SessionFrame.Write(SessionOpcode.Data, payload);
        foreach (NetConnectionId conn in connectionBySlot.Values)
            transport.Send(conn, frame, reliability);
    }
}
