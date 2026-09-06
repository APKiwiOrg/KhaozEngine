using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerNoticeDeliveryTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Broadcast_notice_reaches_a_connected_client()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        ServerNotice? received = null;
        client.NoticeReceived += n => received = n;

        server.BroadcastNotice(new ServerNotice(ServerNoticeKind.Maintenance, "Restarting in 30s", 30f));
        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(received.HasValue, "client never received the notice");
        Assert.Equal(ServerNoticeKind.Maintenance, received!.Value.Kind);
        Assert.Equal("Restarting in 30s", received.Value.Message);
        Assert.Equal(30f, received.Value.SecondsUntil!.Value, 3);
        Assert.True(client.LastNotice.HasValue);
    }
}
