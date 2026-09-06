using System;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

public class ServerDrainTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Begin_drain_broadcasts_the_notice_then_completes_after_the_grace()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        server.BeginDrain(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f), graceSeconds: 1f);
        Assert.True(server.IsDraining);
        Assert.False(server.IsDrainComplete);

        // Pump the grace. The notice is delivered early; completion flips only after the grace elapses.
        bool sawNotice = false;
        for (int i = 0; i < 40; i++)   // 40 * (1/30)s ~= 1.33s > 1s grace
        {
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            if (client.LastNotice.HasValue) sawNotice = true;
        }
        Assert.True(sawNotice, "drain did not broadcast the notice");
        Assert.True(server.IsDrainComplete, "drain never completed after the grace period");
    }
}
