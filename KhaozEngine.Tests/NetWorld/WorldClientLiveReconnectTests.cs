using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

// Reconnect over the REAL LiteNetLib UDP transport, modelling a server deploy: a client connects, the server
// process is killed, a fresh server rebinds the SAME port, and the client's built-in auto-reconnect must land
// back on it. The loopback reconnect tests cannot see this because the in-memory hub hands a fresh, already
// connected endpoint on the first poll; the real transport must complete a UDP handshake against server B.
public class WorldClientLiveReconnectTests
{
    private readonly ITestOutputHelper output;
    public WorldClientLiveReconnectTests(ITestOutputHelper output) => this.output = output;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void Reconnects_over_real_udp_after_a_same_port_server_restart()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };

        // Server A on an OS-assigned free port.
        LiteNetLibServerTransport? serverTransportA = LiveSocketSupport.TryBindServer(out int port);
        if (serverTransportA is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }
        var serverA = new WorldServer(serverTransportA, config, Flat, MoveTuning.Default);

        using var client = new WorldClient(
            () => new LiteNetLibClientTransport("127.0.0.1", port),
            Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = config.TickSeconds,
                DisconnectTimeoutSeconds = 0.5f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.3f },
            });

        // Initial connect over real UDP: join AND ingest the first snapshot (LocalNetId assigned).
        Assert.True(PumpUntil(client, serverA, config,
            () => client.ConnectionState == WorldConnectionState.Connected && client.LocalNetId > 0, 3000),
            "client never made the initial connection");
        output.WriteLine($"initial connect ok on port {port}, netId {client.LocalNetId}");

        // Deploy: kill server A (releases the UDP port), stand up server B on the SAME port.
        serverTransportA.Dispose();
        // Give the OS a beat to release the port and the client to notice the drop.
        Thread.Sleep(200);
        LiteNetLibServerTransport serverTransportB;
        try { serverTransportB = new LiteNetLibServerTransport(port); }
        catch (InvalidOperationException) { output.WriteLine("could not rebind same port; skipping"); return; }
        using (serverTransportB)
        {
            var serverB = new WorldServer(serverTransportB, config, Flat, MoveTuning.Default);

            bool sawReconnecting = false;
            bool reconnected = PumpUntil(client, serverB, config, () =>
            {
                if (client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
                return client.ConnectionState == WorldConnectionState.Connected && sawReconnecting && client.LocalNetId > 0;
            }, 15000);

            output.WriteLine($"sawReconnecting={sawReconnecting}, finalState={client.ConnectionState}, " +
                $"attempt={client.ReconnectAttempt}, netId={client.LocalNetId}, reason={client.DisconnectReason}");

            Assert.True(sawReconnecting, "client never entered Reconnecting");
            Assert.True(reconnected,
                $"client never reconnected to the restarted server (state={client.ConnectionState}, " +
                $"attempt={client.ReconnectAttempt}, reason={client.DisconnectReason} {client.DisconnectReasonDetail})");
        }
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void Reconnects_over_real_udp_after_a_deploy_gap_with_many_failed_attempts()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };

        LiteNetLibServerTransport? serverTransportA = LiveSocketSupport.TryBindServer(out int port);
        if (serverTransportA is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }
        var serverA = new WorldServer(serverTransportA, config, Flat, MoveTuning.Default);

        using var client = new WorldClient(
            () => new LiteNetLibClientTransport("127.0.0.1", port),
            Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = config.TickSeconds,
                DisconnectTimeoutSeconds = 0.5f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.3f },
            });

        Assert.True(PumpUntil(client, serverA, config,
            () => client.ConnectionState == WorldConnectionState.Connected && client.LocalNetId > 0, 3000),
            "client never made the initial connection");

        // Deploy: kill server A and leave the port DOWN for a stretch, so the client burns through MANY failed
        // reconnect attempts (exactly what a slow container restart looks like) before a fresh server appears.
        serverTransportA.Dispose();
        var down = Stopwatch.StartNew();
        int maxAttempt = 0;
        while (down.ElapsedMilliseconds < 5000)
        {
            client.Poll(0.05f);
            client.AdvancePresentation(0.05f);
            maxAttempt = Math.Max(maxAttempt, client.ReconnectAttempt);
            Thread.Sleep(15);
        }
        output.WriteLine($"burned {maxAttempt} reconnect attempts while the server was down");

        // Server B comes up on the SAME port. The client must now land on it.
        LiteNetLibServerTransport serverTransportB;
        try { serverTransportB = new LiteNetLibServerTransport(port); }
        catch (InvalidOperationException) { output.WriteLine("could not rebind same port; skipping"); return; }
        using (serverTransportB)
        {
            var serverB = new WorldServer(serverTransportB, config, Flat, MoveTuning.Default);
            bool reconnected = PumpUntil(client, serverB, config,
                () => client.ConnectionState == WorldConnectionState.Connected && client.LocalNetId > 0, 15000);
            output.WriteLine($"after server B up: state={client.ConnectionState}, attempt={client.ReconnectAttempt}, " +
                $"netId={client.LocalNetId}, reason={client.DisconnectReason} {client.DisconnectReasonDetail}");
            Assert.True(reconnected,
                $"client never reconnected after a deploy gap (state={client.ConnectionState}, " +
                $"attempt={client.ReconnectAttempt}, reason={client.DisconnectReason} {client.DisconnectReasonDetail})");
        }
    }

    // A server that REJECTS every connect (here: a mismatched wire generation, exactly what a client hitting a
    // freshly deployed, protocol-bumped server presents) must surface a TERMINAL disconnect, not an endless
    // reconnect loop. The server sends a reliable Reject then immediately Disconnects the peer; over loopback the
    // Reject is delivered synchronously (terminal), but over a real transport the Disconnect can tear the peer
    // down before the reliable Reject is flushed, so the client sees only a transport drop, reads it as a
    // transient outage, and reconnects forever. That is the "reconnect never succeeds but relaunch works" bug.
    [Trait("Category", "LiveSocket")]
    [Fact]
    public void A_rejecting_server_over_real_udp_goes_terminal_not_endless_reconnect()
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        LiteNetLibServerTransport? st = LiveSocketSupport.TryBindServer(out int port);
        if (st is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }
        // Expect a wire generation no real client sends -> every Hello is rejected with IncompatibleVersion.
        var rejectAuth = new WireGenerationAuthenticator(MoveProtocol.WireProtocolVersion + 1000);
        using (st)
        {
            var server = new WorldServer(st, config, Flat, MoveTuning.Default, authenticator: rejectAuth);
            using var client = new WorldClient(
                () => new LiteNetLibClientTransport("127.0.0.1", port),
                Flat, MoveTuning.Default,
                new WorldClientConfig
                {
                    TickSeconds = config.TickSeconds,
                    DisconnectTimeoutSeconds = 0.5f,
                    Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.3f },
                });

            bool terminal = PumpUntil(client, server, config,
                () => client.ConnectionState == WorldConnectionState.Disconnected, 8000);
            output.WriteLine($"state={client.ConnectionState}, attempt={client.ReconnectAttempt}, " +
                $"reason={client.DisconnectReason} {client.DisconnectReasonDetail}");
            Assert.True(terminal,
                $"client never went terminal on a rejecting server; it is looping in {client.ConnectionState} at " +
                $"attempt {client.ReconnectAttempt} (the reject was lost, so it reconnects forever)");
        }
    }

    // Pumps the client (real dt) and the server (poll+tick) with a small real-time sleep so the background UDP
    // I/O actually flows, until the predicate holds or the wall-clock budget elapses.
    static bool PumpUntil(WorldClient client, WorldServer server, WorldServerConfig config, Func<bool> done, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        long last = 0;
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            long now = sw.ElapsedMilliseconds;
            float dt = (now - last) / 1000f;
            last = now;
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll(dt);
            client.AdvancePresentation(dt);
            if (done()) return true;
            Thread.Sleep(15);
        }
        return done();
    }
}
