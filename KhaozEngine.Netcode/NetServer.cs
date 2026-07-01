using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Session server over an <see cref="INetTransport"/>: accepts connections, runs the Hello/Welcome handshake,
/// authenticates via <see cref="IConnectionAuthenticator"/>, assigns a player slot, and surfaces
/// Joined/Left/Data events (drain with <see cref="TryDequeueEvent"/> after <see cref="Poll"/>).
/// </summary>
public sealed class NetServer
{
    private readonly INetTransport transport;
    private readonly IConnectionAuthenticator authenticator;
    private readonly SlotAllocator slots;
    private readonly Dictionary<int, NetConnectionId> connectionBySlot = new();
    private readonly Dictionary<NetConnectionId, int> slotByConnection = new();
    private readonly BoundedEventQueue<ServerSessionEvent> inbox;

    /// <param name="transport">The byte transport to serve on (already listening).</param>
    /// <param name="maxPlayers">Slot capacity. A Hello past this is answered with a full-server refusal.</param>
    /// <param name="authenticator">Gate for incoming Hello tokens. Returns the verified subject on accept.</param>
    /// <param name="maxQueuedEvents">Defensive hard cap on undrained session events. The drain-to-empty contract
    /// (Poll then drain via <see cref="TryDequeueEvent"/> every tick) keeps this far below the cap; it only bites a
    /// host that stalls or is flooded, where the oldest event is dropped to keep memory bounded (Data events each
    /// pin a payload buffer). Drops are counted in <see cref="DroppedEventCount"/>.</param>
    public NetServer(INetTransport transport, int maxPlayers, IConnectionAuthenticator authenticator,
        int maxQueuedEvents = BoundedEventQueue<ServerSessionEvent>.DefaultCapacity)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        slots = new SlotAllocator(maxPlayers);
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
            transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes(reason)), NetChannelReliability.ReliableOrdered);
            transport.Disconnect(ev.Connection);
            return;
        }
        if (!slots.TryAllocate(out int newSlot))
        {
            transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes("server full")), NetChannelReliability.ReliableOrdered);
            transport.Disconnect(ev.Connection);
            return;
        }
        connectionBySlot[newSlot] = ev.Connection;
        slotByConnection[ev.Connection] = newSlot;
        var slotBytes = new byte[4];
        BitConverter.TryWriteBytes(slotBytes, newSlot);
        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Welcome, slotBytes), NetChannelReliability.ReliableOrdered);
        // Surface a verified display name from the token when the authenticator can provide one (opt-in seam).
        string displayName = authenticator is IConnectionDisplayName named ? named.ReadDisplayName(token) : string.Empty;
        inbox.Enqueue(ServerSessionEvent.Joined(newSlot, token, subject, displayName));
    }

    private void RemovePeer(NetConnectionId conn, int slot)
    {
        slotByConnection.Remove(conn);
        connectionBySlot.Remove(slot);
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
