using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class LiteNetLibTransportTests
{
    [Fact]
    public void Server_ConstructAndDispose_DoesNotThrow()
    {
        using var server = new LiteNetLibServerTransport(port: 0); // 0 = OS-assigned free port
        server.Poll();
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void ClientServer_OverLocalhost_RoundTripsAMessage()
    {
        const int port = 47654;
        using var server = new LiteNetLibServerTransport(port);
        using var client = new LiteNetLibClientTransport("127.0.0.1", port);

        NetConnectionId clientOnServer = PumpUntilId(server, client,
            () => TryFind(server, NetEventType.Connected, out NetConnectionId id) ? id : (NetConnectionId?)null)
            ?? throw new Exception("server never saw the client connect");

        server.Send(clientOnServer, new byte[] { 42 }, NetChannelReliability.ReliableOrdered);

        byte[]? received = PumpUntil(server, client, () => TryFindData(client, out byte[] d) ? d : null);
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 42 }, received);
    }

    // Pumps both transports up to a bounded time budget until the selector returns non-null.
    private static T? PumpUntil<T>(INetTransport a, INetTransport b, Func<T?> selector) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            a.Poll();
            b.Poll();
            T? hit = selector();
            if (hit is not null) return hit;
            Thread.Sleep(15);
        }
        return null;
    }

    private static NetConnectionId? PumpUntilId(INetTransport a, INetTransport b, Func<NetConnectionId?> selector)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            a.Poll();
            b.Poll();
            NetConnectionId? hit = selector();
            if (hit is not null) return hit;
            Thread.Sleep(15);
        }
        return null;
    }

    private static bool TryFind(INetTransport t, NetEventType type, out NetConnectionId id)
    {
        while (t.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == type) { id = ev.Connection; return true; }
        }
        id = NetConnectionId.None;
        return false;
    }

    private static bool TryFindData(INetTransport t, out byte[] data)
    {
        while (t.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == NetEventType.Data) { data = ev.Data; return true; }
        }
        data = Array.Empty<byte>();
        return false;
    }
}
