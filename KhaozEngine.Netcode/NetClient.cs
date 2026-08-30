using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Session client over an <see cref="INetTransport"/>: on transport connect it sends Hello(token), then
/// surfaces Joined(slot)/Rejected(reason)/Data/Disconnected (drain with <see cref="TryDequeueEvent"/> after
/// <see cref="Poll"/>). <see cref="Slot"/> is valid once joined.
/// </summary>
public sealed class NetClient
{
    private readonly INetTransport transport;
    private readonly byte[] token;
    private readonly Queue<ClientSessionEvent> inbox = new();
    // The connection id the transport actually gave the server peer, captured off the Connected event rather than
    // assumed. Both current transports happen to number that peer 1 (LoopbackTransport pins it, and a client-role
    // LiteNetLib NetManager numbers its single peer 0, which the binding surfaces as peer.Id + 1), so a literal 1
    // was indistinguishable from the real id until something renumbered the peer, at which point Send would have
    // aimed at an id no transport knows and silently stopped delivering while receive kept working.
    private NetConnectionId serverConnection = NetConnectionId.None;
    private bool helloSent;

    public NetClient(INetTransport transport, byte[]? token = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.token = token ?? Array.Empty<byte>();
    }

    /// <summary>The assigned slot while this session holds one: set by the Welcome that admitted it, and reset to
    /// -1 the moment the session ends, whether that is a Reject or a transport drop. It is not a high-water mark of
    /// the last slot ever held. A displaced session is the case that made this load-bearing: the duplicate-session
    /// gate answers a post-join Reject rather than a refused Hello
    /// (<see cref="DuplicateSessionPolicy.KickOlder"/>), so a client that kept reporting its old slot would have gone
    /// on naming a seat another session is now sitting in.</summary>
    public int Slot { get; private set; } = -1;

    /// <summary>Live transport statistics (RTT, loss, byte counters); <see cref="NetTransportStats.Unavailable"/> for loopback.</summary>
    public NetTransportStats TransportStats => transport.Stats;

    /// <summary>Pumps the transport: sends Hello on connect, turns Welcome/Reject/Data into session events.</summary>
    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    serverConnection = ev.Connection;   // whatever the transport calls the server, that is what Send targets
                    if (!helloSent)
                    {
                        helloSent = true;
                        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Hello, token), NetChannelReliability.ReliableOrdered);
                    }
                    break;
                case NetEventType.Disconnected:
                    Slot = -1;                       // the session is over either way: stop reporting the seat it held
                    serverConnection = NetConnectionId.None;   // and stop naming a connection the transport has torn down
                    // A disconnect may carry the server's framed Reject as its reason payload (the robust path when
                    // a separately-sent reliable Reject is lost to the teardown over a real socket). Surface it as
                    // the terminal Rejected it is, not a bare drop the consumer would auto-reconnect on.
                    if (SessionFrame.ReadOpcode(ev.Data) == SessionOpcode.Reject)
                        inbox.Enqueue(ClientSessionEvent.Rejected(Encoding.UTF8.GetString(SessionFrame.ReadBody(ev.Data))));
                    else
                        inbox.Enqueue(ClientSessionEvent.Disconnected());
                    break;
                case NetEventType.Data:
                    HandleData(ev);
                    break;
            }
        }
    }

    private void HandleData(NetEvent ev)
    {
        switch (SessionFrame.ReadOpcode(ev.Data))
        {
            case SessionOpcode.Welcome:
                byte[] body = SessionFrame.ReadBody(ev.Data);
                // Little-endian by the wire format, matching the write side; see the note on NetServer's Welcome.
                Slot = body.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(body) : -1;
                inbox.Enqueue(ClientSessionEvent.Joined(Slot));
                break;
            case SessionOpcode.Reject:
                // A Reject can arrive AFTER a Welcome, not only in place of one: the duplicate-session gate displaces
                // a live session that way. Give the slot up rather than keep naming a seat this session has lost.
                Slot = -1;
                inbox.Enqueue(ClientSessionEvent.Rejected(Encoding.UTF8.GetString(SessionFrame.ReadBody(ev.Data))));
                break;
            case SessionOpcode.Data:
                inbox.Enqueue(ClientSessionEvent.FromData(SessionFrame.ReadBody(ev.Data), ev.Reliability));
                break;
        }
    }

    /// <summary>Drains one session event. False when none remain this poll.</summary>
    public bool TryDequeueEvent(out ClientSessionEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    /// <summary>Sends game data to the server, on the connection id the transport reported for it. No-op before the
    /// transport has surfaced a Connected event (there is no server connection to name yet) and after it has surfaced
    /// the Disconnected that ended the session.</summary>
    public void Send(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (!serverConnection.IsValid) return;
        transport.Send(serverConnection, SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }
}
