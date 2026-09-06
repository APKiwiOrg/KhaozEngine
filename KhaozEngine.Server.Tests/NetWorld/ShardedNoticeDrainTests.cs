using System;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedNoticeDrainTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Sharded_broadcast_reaches_a_client_and_drain_completes()
    {
        var hub = new InMemoryTransportHub();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 24f, OverlapMargin = 24f, MaxPlayers = 16 };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        server.BeginDrain(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f), graceSeconds: 1f);
        bool sawNotice = false;
        for (int i = 0; i < 40; i++)
        {
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            if (client.LastNotice.HasValue) sawNotice = true;
        }
        Assert.True(sawNotice);
        Assert.True(server.IsDrainComplete);
    }
}
