using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Netcode;

public class LiteNetLibTransportTests
{
    private readonly ITestOutputHelper output;
    public LiteNetLibTransportTests(ITestOutputHelper output) => this.output = output;

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
        // OS-assigned ephemeral port, never a fixed one - a hardcoded port collides with any other process
        // (a stale server, a parallel test run) that happens to hold it.
        using LiteNetLibServerTransport? server = LiveSocketSupport.TryBindServer(out int port);
        if (server is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }
        using var client = new LiteNetLibClientTransport("127.0.0.1", port);

        NetConnectionId clientOnServer = PumpUntilId(server, client,
            () => TryFind(server, NetEventType.Connected, out NetConnectionId id) ? id : (NetConnectionId?)null)
            ?? throw new Exception("server never saw the client connect");

        server.Send(clientOnServer, new byte[] { 42 }, NetChannelReliability.ReliableOrdered);

        byte[]? received = PumpUntil(server, client, () => TryFindData(client, out byte[] d) ? d : null);
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 42 }, received);
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void ServerInbox_UnderOverflow_KeepsTheDisconnect_NotTheNewerEvent()
    {
        // A cap of 1 makes one later event enough to overflow the inbox, so the eviction a real flood causes is
        // reproducible without pushing thousands of packets through a socket.
        using LiteNetLibServerTransport? server = LiveSocketSupport.TryBindServer(out int port, maxQueuedEvents: 1);
        if (server is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }

        using var first = new LiteNetLibClientTransport("127.0.0.1", port);
        NetConnectionId firstId = PumpUntilId(server, first,
            () => TryFind(server, NetEventType.Connected, out NetConnectionId id) ? id : (NetConnectionId?)null)
            ?? throw new Exception("server never saw the first client connect");

        // Drop the peer from the SERVER side: LiteNetLib raises the local Disconnected on the next poll, with no
        // packet to wait on, so it is in the inbox before anything newer can arrive.
        server.Disconnect(firstId);
        for (int i = 0; i < 4; i++) { server.Poll(); Thread.Sleep(15); }

        // A second client's Connected is the newer event that overflows the cap-1 inbox. Its arrival is observed
        // on the CLIENT's inbox so the server's stays undrained, which is the whole point.
        using var second = new LiteNetLibClientTransport("127.0.0.1", port);
        Assert.NotNull(PumpUntil(server, second,
            () => TryFind(second, NetEventType.Connected, out _) ? "up" : null));

        var drained = new System.Collections.Generic.List<NetEvent>();
        while (server.TryDequeueEvent(out NetEvent ev)) drained.Add(ev);

        // The Disconnected is the OLDEST buffered event, so drop-oldest is exactly what would throw it away, and
        // NetServer releases the peer's slot off this event and off nothing else.
        Assert.NotEmpty(drained);
        Assert.Equal(NetEventType.Disconnected, drained[0].Type);
        Assert.Equal(firstId, drained[0].Connection);
        Assert.Contains(drained, e => e.Type == NetEventType.Connected);
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
