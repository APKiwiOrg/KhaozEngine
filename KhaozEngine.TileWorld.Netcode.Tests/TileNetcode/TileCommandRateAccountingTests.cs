using System;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCommandRateAccountingTests
{
    const float Tick = 0.25f;

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Empty_polls_and_non_ticks_cannot_refill_the_command_budget(int emptyPolls)
    {
        (TileWorldServer server, NetClient client) = Connect(burst: 1);
        using (server)
        {
            int accepted = 0;
            server.OnGameMessage += (_, _, _) => accepted++;

            Send(server, client, count: 1);
            Assert.Equal(1, accepted);
            for (int i = 0; i < emptyPolls; i++) server.Poll();
            Send(server, client, count: 1);
            Assert.Equal(1, accepted);

            server.Tick(-1f);
            server.Tick(0f);
            Send(server, client, count: 1);
            Assert.Equal(1, accepted);
            server.Tick(Tick);
            Send(server, client, count: 1);
            Assert.Equal(2, accepted);
        }
    }

    [Fact]
    public void A_catch_up_frame_refills_once_for_each_simulated_tick()
    {
        (TileWorldServer server, NetClient client) = Connect(burst: 4);
        using (server)
        {
            int accepted = 0;
            server.OnGameMessage += (_, _, _) => accepted++;

            Send(server, client, count: 4);
            Assert.Equal(4, accepted);

            server.Tick(1f);
            Send(server, client, count: 4);
            Assert.Equal(8, accepted);
        }
    }

    static (TileWorldServer server, NetClient client) Connect(int burst)
    {
        var hub = new InMemoryTransportHub();
        TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(4, 4, 0)) with
        {
            TickSeconds = Tick,
            MaxCommandsPerSecond = 4,
            CommandBurst = burst,
        };
        var server = new TileWorldServer(hub.Server, config,
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), null, new AllowAllAuthenticator());
        var client = new NetClient(hub.CreateClient(), Array.Empty<byte>());
        for (int i = 0; i < 20 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
        }
        Assert.Equal(1, server.PlayerCount);
        return (server, client);
    }

    static void Send(TileWorldServer server, NetClient client, int count)
    {
        for (int i = 0; i < count; i++)
            client.Send(TileProtocol.EncodeGameMessage(
                TileProtocol.ClientFrameGameMessage, 1, ReadOnlySpan<byte>.Empty),
                NetChannelReliability.ReliableOrdered);
        client.Poll();
        server.Poll();
    }
}
