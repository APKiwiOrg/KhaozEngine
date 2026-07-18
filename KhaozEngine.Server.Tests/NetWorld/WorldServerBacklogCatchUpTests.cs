using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end guard for the reported reconnect symptom: a deep per-player input backlog (here built by bursting moves
/// without ticking the server between them, as a flush/lag-burst would) must not drive the authoritative avatar one
/// stale move per tick for the whole backlog. With <see cref="WorldServerConfig.MaxInputBacklog"/> the server catches
/// up to live in a single tick; with it disabled the same backlog crawls out one tick at a time (the old behaviour).
/// </summary>
public class WorldServerBacklogCatchUpTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
    const int Burst = 120;

    static WorldServer NewServer(KhaozEngine.Netcode.INetTransport t, WorldServerConfig config) =>
        new(t, config, Flat, MoveTuning.Default);

    // Connect, then queue Burst forward moves WITHOUT ticking the server, so they pile into one slot's queue.
    // Returns the number of ticks the authoritative avatar kept advancing (i.e. how long it drained stale input).
    static int TicksSpentDrainingBacklog(int maxInputBacklog)
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig
        {
            TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 4, MaxInputBacklog = maxInputBacklog,
        };
        var server = NewServer(hub.Server, config);
        using var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // Pile up a deep backlog: send every move first, THEN let the server ingest them all in one Poll.
        for (int i = 0; i < Burst; i++) client.SendInput(Forward);
        server.Poll();

        // Now drain. Count ticks where the authoritative player keeps moving forward (consuming queued input).
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState prev));
        int movingTicks = 0;
        for (int i = 0; i < Burst + 20; i++)
        {
            server.Tick(config.TickSeconds);
            server.Poll();
            Assert.True(server.TryGetPlayerState(0, out PlayerMoveState now));
            if (MathF.Abs(now.Position.Z - prev.Position.Z) > 1e-4f) movingTicks++;
            prev = now;
        }
        return movingTicks;
    }

    [Fact]
    public void DeepBacklog_CatchesUp_InsteadOfCrawling()
    {
        int withCatchUp = TicksSpentDrainingBacklog(maxInputBacklog: 8);
        int withoutCatchUp = TicksSpentDrainingBacklog(maxInputBacklog: 0);

        // Disabled: the backlog crawls out roughly one move per tick (the bug - minutes of stale input on a real
        // outage). Enabled: it collapses to live in a single tick.
        Assert.True(withoutCatchUp > Burst / 2,
            $"expected the uncapped server to crawl through the backlog, drained in only {withoutCatchUp} ticks");
        Assert.True(withCatchUp <= 2,
            $"expected catch-up to reach live in ~1 tick, took {withCatchUp}");
    }
}
