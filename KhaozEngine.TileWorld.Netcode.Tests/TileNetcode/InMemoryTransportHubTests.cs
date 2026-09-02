using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class InMemoryTransportHubTests
{
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    static string Drain(INetTransport t, out int connectedCount)
    {
        connectedCount = 0;
        var sb = new StringBuilder();
        t.Poll();
        while (t.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == NetEventType.Connected) connectedCount++;
            if (ev.Type == NetEventType.Data) sb.Append(Encoding.UTF8.GetString(ev.Data));
        }
        return sb.ToString();
    }

    [Fact]
    public void Two_clients_get_distinct_connection_ids_and_only_their_own_bytes()
    {
        var hub = new InMemoryTransportHub();
        INetTransport a = hub.CreateClient();
        INetTransport b = hub.CreateClient();

        a.Send(new NetConnectionId(1), Bytes("from-a"), NetChannelReliability.ReliableOrdered);
        b.Send(new NetConnectionId(1), Bytes("from-b"), NetChannelReliability.ReliableOrdered);

        hub.Server.Poll();
        var seen = new System.Collections.Generic.List<(int conn, string body)>();
        while (hub.Server.TryDequeueEvent(out NetEvent ev))
            if (ev.Type == NetEventType.Data) seen.Add((ev.Connection.Value, Encoding.UTF8.GetString(ev.Data)));

        Assert.Equal(2, seen.Count);
        Assert.Equal((1, "from-a"), seen[0]);
        Assert.Equal((2, "from-b"), seen[1]);

        hub.Server.Send(new NetConnectionId(2), Bytes("only-b"), NetChannelReliability.ReliableOrdered);
        Assert.Equal(string.Empty, Drain(a, out _));
        Assert.Equal("only-b", Drain(b, out _));
    }

    [Fact]
    public void Each_endpoint_announces_its_peer_exactly_once()
    {
        var hub = new InMemoryTransportHub();
        INetTransport a = hub.CreateClient();
        hub.Server.Poll();
        int serverConnects = 0;
        while (hub.Server.TryDequeueEvent(out NetEvent ev)) if (ev.Type == NetEventType.Connected) serverConnects++;

        Drain(a, out int clientConnects);
        Drain(a, out int clientConnectsAgain);

        Assert.Equal(1, serverConnects);
        Assert.Equal(1, clientConnects);
        Assert.Equal(0, clientConnectsAgain);
    }

    [Fact]
    public void Disconnecting_a_client_surfaces_on_both_sides_and_stops_its_traffic()
    {
        var hub = new InMemoryTransportHub();
        INetTransport a = hub.CreateClient();
        Drain(a, out _);
        hub.Server.Poll();
        while (hub.Server.TryDequeueEvent(out _)) { }

        hub.DisconnectClient(a);
        a.Send(new NetConnectionId(1), Bytes("ignored"), NetChannelReliability.ReliableOrdered);

        hub.Server.Poll();
        bool serverSawDrop = false;
        bool serverSawData = false;
        while (hub.Server.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == NetEventType.Disconnected) serverSawDrop = true;
            if (ev.Type == NetEventType.Data) serverSawData = true;
        }

        bool clientSawDrop = false;
        a.Poll();
        while (a.TryDequeueEvent(out NetEvent ev)) if (ev.Type == NetEventType.Disconnected) clientSawDrop = true;

        Assert.True(serverSawDrop);
        Assert.False(serverSawData);
        Assert.True(clientSawDrop);
    }

    // A head that drops its OWN link. Both endpoints used to answer INetTransport.Disconnect with an empty body,
    // so a kick reached nothing and every test that needed one compensated with an explicit hub.DisconnectClient
    // beside it. That is the one shape a real transport gets right and the harness did not, so a regression in a
    // server-initiated kick would have shown up nowhere.
    [Theory]
    [InlineData(true)]     // the server kicks the client
    [InlineData(false)]    // the client hangs up on the server
    public void A_disconnect_through_either_endpoint_drops_the_link_on_both_sides(bool serverInitiated)
    {
        var hub = new InMemoryTransportHub();
        INetTransport a = hub.CreateClient();
        Drain(a, out _);
        hub.Server.Poll();
        while (hub.Server.TryDequeueEvent(out _)) { }

        // Sent BEFORE the drop, so this also pins the ordering LoopbackTransport.Disconnect promises: a plain kick
        // loses nothing, and whatever the peer was already handed still surfaces ahead of the Disconnected.
        hub.Server.Send(new NetConnectionId(1), Bytes("last-word"), NetChannelReliability.ReliableOrdered);
        if (serverInitiated) hub.Server.Disconnect(new NetConnectionId(1));
        else a.Disconnect(new NetConnectionId(1));

        var clientEvents = new System.Collections.Generic.List<NetEventType>();
        a.Poll();
        while (a.TryDequeueEvent(out NetEvent ev))
        {
            clientEvents.Add(ev.Type);
            if (ev.Type == NetEventType.Data) Assert.Equal("last-word", Encoding.UTF8.GetString(ev.Data));
        }
        Assert.Equal(new[] { NetEventType.Data, NetEventType.Disconnected }, clientEvents);

        bool serverSawDrop = false;
        hub.Server.Poll();
        while (hub.Server.TryDequeueEvent(out NetEvent ev)) if (ev.Type == NetEventType.Disconnected) serverSawDrop = true;
        Assert.True(serverSawDrop);

        // And it is idempotent, because the tests that already compensate with an explicit DisconnectClient still
        // run it beside the kick: a second drop must not deliver a second Disconnected to either end.
        hub.DisconnectClient(a);
        a.Poll();
        Assert.False(a.TryDequeueEvent(out _));
        hub.Server.Poll();
        Assert.False(hub.Server.TryDequeueEvent(out _));
    }
}
