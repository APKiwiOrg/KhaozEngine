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
    private readonly Queue<ServerSessionEvent> inbox = new();

    public NetServer(INetTransport transport, int maxPlayers, IConnectionAuthenticator authenticator)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        slots = new SlotAllocator(maxPlayers);
    }

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
    public bool TryDequeueEvent(out ServerSessionEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    /// <summary>Sends game data to one slot. No-op for an unknown slot.</summary>
    public void SendTo(int slot, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            transport.Send(conn, SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }

    /// <summary>Sends game data to every joined slot.</summary>
    public void Broadcast(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] frame = SessionFrame.Write(SessionOpcode.Data, payload);
        foreach (NetConnectionId conn in connectionBySlot.Values)
            transport.Send(conn, frame, reliability);
    }
}
