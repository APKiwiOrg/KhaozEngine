using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CommandRateAccountingTests
{
    const float Tick = 0.25f;

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void World_server_refills_from_simulated_time_independent_of_poll_rate(int emptyPolls)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var server = new WorldServer(serverTransport, new WorldServerConfig
        {
            TickSeconds = Tick,
            AntiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 4f, MessageBurst = 1f },
        }, static (_, _) => 0f, MoveTuning.Default);
        var client = new NetClient(clientTransport, TestHandshake.Wire());
        Connect(server, client);
        int accepted = 0;
        server.OnGameMessage += (_, _, _) => accepted++;

        Send(server, client);
        Assert.Equal(1, accepted);
        for (int i = 0; i < emptyPolls; i++) server.Poll();
        Send(server, client);
        Assert.Equal(1, accepted);

        server.Tick(0f);
        Send(server, client);
        Assert.Equal(1, accepted);
        server.Tick(Tick);
        Send(server, client);
        Assert.Equal(2, accepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Sharded_server_refills_only_when_its_fixed_clock_steps(int emptyPolls)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = Tick,
            CellSize = 64f,
            OverlapMargin = 8f,
            InterestRadius = 8f,
            AntiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 4f, MessageBurst = 1f },
        };
        using var server = new ShardedWorldServer(
            serverTransport, config, static (_, _) => 0f, MoveTuning.Default);
        var client = new NetClient(clientTransport, TestHandshake.Wire());
        Connect(server, client);
        int accepted = 0;
        server.OnGameMessage += (_, _, _) => accepted++;

        Send(server, client);
        Assert.Equal(1, accepted);
        for (int i = 0; i < emptyPolls; i++) server.Poll();
        Send(server, client);
        Assert.Equal(1, accepted);

        server.Tick(Tick / 2f);
        Send(server, client);
        Assert.Equal(1, accepted);
        server.Tick(Tick / 2f);
        Send(server, client);
        Assert.Equal(2, accepted);
    }

    static void Connect(WorldServer server, NetClient client)
    {
        for (int i = 0; i < 20 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
        }
        Assert.Equal(1, server.PlayerCount);
    }

    static void Connect(ShardedWorldServer server, NetClient client)
    {
        for (int i = 0; i < 20 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
        }
        Assert.Equal(1, server.PlayerCount);
    }

    static void Send(WorldServer server, NetClient client)
    {
        client.Send(MoveProtocol.EncodeGameMessage(1, ReadOnlySpan<byte>.Empty),
            NetChannelReliability.ReliableOrdered);
        client.Poll();
        server.Poll();
    }

    static void Send(ShardedWorldServer server, NetClient client)
    {
        client.Send(MoveProtocol.EncodeGameMessage(1, ReadOnlySpan<byte>.Empty),
            NetChannelReliability.ReliableOrdered);
        client.Poll();
        server.Poll();
    }
}
