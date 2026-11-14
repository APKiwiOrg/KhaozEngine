using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientTimeoutTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // #1790: both timeout paths end the server session before any reconnect backoff elapses.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Timeout_ends_server_session_at_once(bool reconnect, bool joinDeadline)
    {
        using var hub = new InMemoryTransportHub();
        var server = new NetServer(hub.Server, maxPlayers: 4, new AllowAllAuthenticator());
        PausedReceiveTransport? current = null;
        INetTransport Connect() => current = new PausedReceiveTransport(hub.CreateClient());
        var config = new WorldClientConfig
        {
            DisconnectTimeoutSeconds = 1f,
            Reconnect = new ReconnectBackoff { InitialSeconds = 0.5f, Multiplier = 2f, MaxSeconds = 2f },
        };
        using var client = reconnect
            ? new WorldClient(Connect, Flat, MoveTuning.Default, config)
            : new WorldClient(Connect(), Flat, MoveTuning.Default, config);
        PausedReceiveTransport transport = current!;
        var states = new List<WorldConnectionState>();
        client.ConnectionStateChanged += states.Add;

        client.Poll();   // Send Hello, then hold Welcome to exercise the join deadline with a live server session.
        transport.Paused = joinDeadline;
        server.Poll();
        Assert.True(server.TryDequeueEvent(out ServerSessionEvent joined));
        Assert.Equal(ServerSessionEventKind.Joined, joined.Kind);
        client.Poll();
        Assert.Equal(joinDeadline ? WorldConnectionState.Connecting : WorldConnectionState.Connected,
            client.ConnectionState);

        client.Poll(1f);   // No snapshots, or no Welcome, for the exact configured timeout window.
        WorldConnectionState ended = reconnect ? WorldConnectionState.Reconnecting : WorldConnectionState.Disconnected;
        Assert.Equal(ended, client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, client.DisconnectReason);
        Assert.False(client.Joined);
        server.Poll();
        var leaves = new List<ServerSessionEvent>();
        while (server.TryDequeueEvent(out ServerSessionEvent ev))
            if (ev.Kind == ServerSessionEventKind.Left) leaves.Add(ev);
        ServerSessionEvent left = Assert.Single(leaves);
        Assert.Equal(joined.Slot, left.Slot);

        // The hub queues our own disconnect. A held Welcome may precede it. Neither can revive or fail the attempt again.
        transport.Paused = false;
        client.Poll();
        Assert.Equal(ended, client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, client.DisconnectReason);
        Assert.Equal(reconnect ? 1 : 0, client.ReconnectAttempt);
        Assert.Equal(reconnect ? 0.5f : 0f, client.SecondsUntilNextRetry);
        Assert.Equal(joinDeadline ? new[] { ended } : new[] { WorldConnectionState.Connected, ended }, states);

        if (reconnect)
        {
            client.Poll(0.25f);
            Assert.Equal(0.25f, client.SecondsUntilNextRetry);
            Assert.Equal(1, client.ReconnectAttempt);
            client.Poll(0.25f);   // The next attempt starts only when the original backoff ends.
            server.Poll();
            Assert.True(server.TryDequeueEvent(out ServerSessionEvent rejoined));
            Assert.Equal(ServerSessionEventKind.Joined, rejoined.Kind);
            client.Poll();
            Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
            Assert.Equal(DisconnectReason.None, client.DisconnectReason);
        }
        else
        {
            client.Dispose();
            transport.Poll();   // Ending and disposing the client leave its caller-owned transport usable.
            transport.Dispose();
        }
    }

    // #1790: a deadline may also expire before a transport has reported any connection id.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Join_deadline_without_a_transport_connection_is_safe(bool reconnect)
    {
        using var hub = new InMemoryTransportHub();
        using var transport = new PausedReceiveTransport(hub.CreateClient()) { Paused = true };
        var config = new WorldClientConfig { DisconnectTimeoutSeconds = 1f };
        using var client = reconnect
            ? new WorldClient(() => transport, Flat, MoveTuning.Default, config)
            : new WorldClient(transport, Flat, MoveTuning.Default, config);

        client.Poll(1f);

        Assert.Equal(reconnect ? WorldConnectionState.Reconnecting : WorldConnectionState.Disconnected,
            client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, client.DisconnectReason);
        Assert.False(client.Joined);
    }

    [Fact]
    public void Drop_after_a_shutdown_notice_is_attributed_ServerShutdown()
    {
        var hub = new InMemoryTransportHub();
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

    /// <summary>The ban mirror of the shutdown case above. The server's own ban path sends the typed
    /// <see cref="ServerNoticeKind.Banned"/> notice at join, and the drop that follows must attribute as
    /// <see cref="DisconnectReason.Banned"/> rather than the generic <see cref="DisconnectReason.Unreachable"/> a
    /// crash or a network loss gives, so a consumer reading only the reason can tell a ban from an outage. The
    /// in-memory transport carries both the notice frame and the server's terminal event through the real kick
    /// path.</summary>
    [Fact]
    public async Task Drop_after_a_banned_notice_is_attributed_Banned()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default, banStore: bans);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 5f },
            token: Encoding.UTF8.GetBytes("evil"));

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(0, server.PlayerCount);
        Assert.True(client.LastNotice.HasValue);
        Assert.Equal(ServerNoticeKind.Banned, client.LastNotice!.Value.Kind);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) client.Poll();
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.Banned, client.DisconnectReason);
    }

    /// <summary>The gate is the notice, not the drop: a drop with nothing in front of it still reads as
    /// <see cref="DisconnectReason.Unreachable"/>.</summary>
    [Fact]
    public void Drop_with_no_notice_in_front_of_it_is_still_Unreachable()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 5f });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) client.Poll();
        Assert.Equal(DisconnectReason.Unreachable, client.DisconnectReason);
    }
}
