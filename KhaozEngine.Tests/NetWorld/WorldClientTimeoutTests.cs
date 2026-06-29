using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientTimeoutTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void No_snapshots_for_the_timeout_window_is_Disconnected_Timeout()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 1f });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // Stop ticking the server (no more snapshots) and advance the client's clock past the timeout.
        for (int i = 0; i < 40; i++) client.Poll(0.05f);   // 2.0s of dt > 1s timeout
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, client.DisconnectReason);
    }

    [Fact]
    public void Drop_after_a_shutdown_notice_is_attributed_ServerShutdown()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 5f });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        server.BroadcastNotice(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f));
        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.LastNotice.HasValue);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) client.Poll();
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ServerShutdown, client.DisconnectReason);
    }
}
