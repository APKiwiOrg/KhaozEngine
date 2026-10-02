using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PreciseMovementWireHandshakeTests
{
    private const float Tick = 1f / 30f;
    private static float Flat(float x, float z) => 0f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationTwelveHelloIsRejectedBeforePlayerAdmission(bool sharded)
    {
        var result = Hello(sharded, ProtocolHandshake.BuildClientToken(12, consumerVersion: null, null));

        Assert.Equal(-1, result.Slot);
        Assert.Equal(0, result.Players);
        Assert.DoesNotContain(result.Events, ev => ev.Kind == ClientSessionEventKind.Joined);
        Assert.DoesNotContain(result.Events, ev => ev.Kind == ClientSessionEventKind.Data);
        ClientSessionEvent rejected = Assert.Single(result.Events, ev => ev.Kind == ClientSessionEventKind.Rejected);
        ConnectRefusal refusal = ConnectRefusal.Read(rejected.RejectReason);
        Assert.Equal(DisconnectReason.IncompatibleVersion, refusal.Reason);
        Assert.Equal(ProtocolHandshake.WireGenerationLabel(13), refusal.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationThirteenHelloJoinsAndReceivesSnapshots(bool sharded)
    {
        var result = Hello(sharded, ProtocolHandshake.BuildClientToken(13, consumerVersion: null, null));

        Assert.True(result.Slot >= 0);
        Assert.Equal(1, result.Players);
        Assert.Single(result.Events, ev => ev.Kind == ClientSessionEventKind.Joined);
        Assert.Contains(result.Events, ev => ev.Kind == ClientSessionEventKind.Data);
        Assert.DoesNotContain(result.Events, ev => ev.Kind == ClientSessionEventKind.Rejected);
    }

    private static (int Slot, int Players, List<ClientSessionEvent> Events) Hello(bool sharded, byte[] token)
    {
        var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        var client = new NetClient(transport, token);
        if (sharded)
        {
            using var server = new ShardedWorldServer(hub.Server,
                new ShardedWorldServerConfig { TickSeconds = Tick, MaxPlayers = 8 }, Flat, MoveTuning.Default);
            List<ClientSessionEvent> events = Pump(client, server.Poll, server.Tick);
            return (client.Slot, server.PlayerCount, events);
        }
        else
        {
            var server = new WorldServer(hub.Server,
                new WorldServerConfig { TickSeconds = Tick, MaxPlayers = 8 }, Flat, MoveTuning.Default);
            List<ClientSessionEvent> events = Pump(client, server.Poll, server.Tick);
            return (client.Slot, server.PlayerCount, events);
        }
    }

    private static List<ClientSessionEvent> Pump(NetClient client, Action poll, Action<float> tick)
    {
        var events = new List<ClientSessionEvent>();
        for (int i = 0; i < 10; i++)
        {
            client.Poll();
            poll();
            tick(Tick);
            client.Poll();
            while (client.TryDequeueEvent(out ClientSessionEvent ev)) events.Add(ev);
        }
        return events;
    }
}
