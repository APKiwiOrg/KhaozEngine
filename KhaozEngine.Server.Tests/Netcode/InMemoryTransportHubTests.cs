using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class InMemoryTransportHubTests
{
    private static readonly NetConnectionId ServerId = new(1);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void Multiple_clients_get_distinct_ids_and_targeted_delivery()
    {
        using var hub = new InMemoryTransportHub();
        INetTransport first = hub.CreateClient();
        INetTransport second = hub.CreateClient();

        first.Send(ServerId, Bytes("a"), NetChannelReliability.ReliableOrdered);
        second.Send(ServerId, Bytes("b"), NetChannelReliability.UnreliableSequenced);
        List<NetEvent> serverEvents = Drain(hub.Server);

        Assert.Equal(NetEventType.Connected, serverEvents[0].Type);
        Assert.Equal(1, serverEvents[0].Connection.Value);
        Assert.Equal(NetEventType.Connected, serverEvents[1].Type);
        Assert.Equal(2, serverEvents[1].Connection.Value);
        Assert.Equal("a", Text(serverEvents[2]));
        Assert.Equal(1, serverEvents[2].Connection.Value);
        Assert.Equal("b", Text(serverEvents[3]));
        Assert.Equal(2, serverEvents[3].Connection.Value);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, serverEvents[3].Reliability);

        hub.Server.Send(new NetConnectionId(2), Bytes("second"), NetChannelReliability.ReliableOrdered);
        Assert.DoesNotContain(Drain(first), e => e.Type == NetEventType.Data);
        Assert.Contains(Drain(second), e => e.Type == NetEventType.Data && Text(e) == "second");
        Assert.DoesNotContain(Drain(second), e => e.Type == NetEventType.Connected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plain_disconnect_preserves_prior_data_then_terminates_each_side_once(bool serverInitiated)
    {
        using var hub = new InMemoryTransportHub();
        INetTransport client = hub.CreateClient();
        Drain(hub.Server);
        Drain(client);

        hub.Server.Send(new NetConnectionId(1), Bytes("last"), NetChannelReliability.ReliableOrdered);
        client.Send(ServerId, Bytes("reply"), NetChannelReliability.ReliableOrdered);
        if (serverInitiated) hub.Server.Disconnect(new NetConnectionId(1));
        else client.Disconnect(ServerId);

        Assert.Equal(
            new[] { NetEventType.Data, NetEventType.Disconnected },
            Drain(client).ConvertAll(e => e.Type));
        Assert.Equal(
            new[] { NetEventType.Data, NetEventType.Disconnected },
            Drain(hub.Server).ConvertAll(e => e.Type));

        client.Send(ServerId, Bytes("late"), NetChannelReliability.ReliableOrdered);
        hub.Server.Send(new NetConnectionId(1), Bytes("late"), NetChannelReliability.ReliableOrdered);
        hub.DisconnectClient(client);
        Assert.Empty(Drain(client));
        Assert.Empty(Drain(hub.Server));
    }

    [Fact]
    public void Reason_disconnect_supersedes_only_that_clients_undelivered_frames()
    {
        using var hub = new InMemoryTransportHub();
        INetTransport first = hub.CreateClient();
        INetTransport second = hub.CreateClient();
        Drain(hub.Server);
        Drain(first);
        Drain(second);

        first.Send(ServerId, Bytes("discard-first"), NetChannelReliability.ReliableOrdered);
        second.Send(ServerId, Bytes("keep-second"), NetChannelReliability.ReliableOrdered);
        first.Disconnect(ServerId, Bytes("reason"));

        List<NetEvent> events = Drain(hub.Server);
        Assert.DoesNotContain(events, e => e.Type == NetEventType.Data && e.Connection.Value == 1);
        Assert.Contains(events, e => e.Type == NetEventType.Data && Text(e) == "keep-second");
        NetEvent terminal = Assert.Single(events, e => e.Type == NetEventType.Disconnected);
        Assert.Equal("reason", Text(terminal));
        Assert.Empty(Drain(second));
    }

    [Fact]
    public void Dispose_disconnects_peers_and_stops_new_connections()
    {
        var hub = new InMemoryTransportHub();
        INetTransport first = hub.CreateClient();
        INetTransport second = hub.CreateClient();
        Drain(hub.Server);
        Drain(first);
        Drain(second);

        first.Dispose();
        Assert.Equal(NetEventType.Disconnected, Assert.Single(Drain(hub.Server)).Type);

        hub.Server.Send(new NetConnectionId(2), Bytes("last"), NetChannelReliability.ReliableOrdered);
        hub.Dispose();
        Assert.Equal(
            new[] { NetEventType.Data, NetEventType.Disconnected },
            Drain(second).ConvertAll(e => e.Type));
        Assert.Throws<ObjectDisposedException>(() => hub.CreateClient());

        hub.Dispose();
        Assert.Empty(Drain(second));
    }

    private static List<NetEvent> Drain(INetTransport transport)
    {
        transport.Poll();
        var events = new List<NetEvent>();
        while (transport.TryDequeueEvent(out NetEvent value)) events.Add(value);
        return events;
    }

    private static string Text(NetEvent value) => Encoding.UTF8.GetString(value.Data);
}
