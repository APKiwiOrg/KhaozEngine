using System;
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
    private bool helloSent;

    public NetClient(INetTransport transport, byte[]? token = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.token = token ?? Array.Empty<byte>();
    }

    /// <summary>The assigned slot once <see cref="ClientSessionEventKind.Joined"/> has been observed, else -1.</summary>
    public int Slot { get; private set; } = -1;

    /// <summary>Pumps the transport: sends Hello on connect, turns Welcome/Reject/Data into session events.</summary>
    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    if (!helloSent)
                    {
                        helloSent = true;
                        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Hello, token), NetChannelReliability.ReliableOrdered);
                    }
                    break;
                case NetEventType.Disconnected:
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
                Slot = body.Length >= 4 ? BitConverter.ToInt32(body, 0) : -1;
                inbox.Enqueue(ClientSessionEvent.Joined(Slot));
                break;
            case SessionOpcode.Reject:
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

    /// <summary>Sends game data to the server (surfaced as connection id 1 by loopback and the UDP bindings).</summary>
    public void Send(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        transport.Send(new NetConnectionId(1), SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }
}
