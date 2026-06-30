using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

public class AdminHttpServerTests
{
    private static float Flat(float x, float z) => 0f;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Requires_Bearer_And_Serves_Online()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);

        var admin = new ServerAdmin(server);
        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        string baseUrl = $"https://127.0.0.1:{port}/admin";

        HttpResponseMessage noAuth = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage ok = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("alice", body);

        await http.StopAsync();
    }

    /// <summary>
    /// GET /online must serialize OnlinePlayer.Position (a System.Numerics.Vector3, whose X/Y/Z are FIELDS, not
    /// properties) with its components. System.Text.Json drops fields by default, so without a scoped converter the
    /// position serializes as an empty object and every consumer reads a zero position. Round-trips a NON-ZERO
    /// position through the live endpoint to prove the components survive the wire.
    /// </summary>
    [Fact]
    public async Task GetOnline_SerializesNonZeroPosition()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("bob"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        Assert.Equal(1, server.PlayerCount);

        var admin = new ServerAdmin(server);

        // Teleport the connected player off the origin and tick so the authoritative move + published online snapshot
        // both carry the new position.
        var moved = new System.Numerics.Vector3(12.5f, 0f, -7.25f);
        admin.Teleport(PlayerRef.Slot(0), moved);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }

        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        HttpResponseMessage ok = await hc.GetAsync($"https://127.0.0.1:{port}/admin/online");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string body = await ok.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement player = doc.RootElement[0];
        System.Text.Json.JsonElement pos = player.GetProperty("position");

        // The bug: position serializes as {} (no x/y/z), so consumers read a zero position.
        Assert.Equal(System.Text.Json.JsonValueKind.Object, pos.ValueKind);
        Assert.True(pos.TryGetProperty("x", out var px), $"position has no 'x' component: {pos.GetRawText()}");
        Assert.True(pos.TryGetProperty("z", out var pz), $"position has no 'z' component: {pos.GetRawText()}");
        Assert.Equal(12.5f, px.GetSingle(), 3);
        Assert.Equal(-7.25f, pz.GetSingle(), 3);

        await http.StopAsync();
    }

    /// <summary>
    /// POST /broadcast exercises BroadcastRequest DTO binding (the internal-DTO-vs-minimal-API-JSON-binding risk)
    /// and verifies end-to-end notice delivery to a connected WorldClient.
    /// </summary>
    [Fact]
    public async Task Broadcast_Returns202_And_Delivers_Notice_To_Client()
    {
        // Arrange: server + WorldClient (handles notice decoding via LastNotice).
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        using var wc = new WorldClient(ct, Flat, MoveTuning.Default,
            token: Encoding.UTF8.GetBytes("alice"));

        // Connect the client.
        for (int i = 0; i < 200 && !wc.Joined; i++)
        {
            wc.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        Assert.True(wc.Joined, "WorldClient did not join within the connection budget.");

        // Start AdminHttpServer.
        var admin = new ServerAdmin(server);
        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        string baseUrl = $"https://127.0.0.1:{port}/admin";

        // Act: POST /broadcast with a JSON body - exercises BroadcastRequest binding.
        var content = new StringContent("{\"Text\":\"hello\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await hc.PostAsync(baseUrl + "/broadcast", content);

        // Assert: 202 Accepted (binding succeeded; command enqueued).
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Drain the admin command buffer via Tick, then poll the client until the notice arrives.
        // Tick applies the enqueued Broadcast command (calls BroadcastNotice -> sends on the Data channel);
        // WorldClient.Poll processes the server frame and raises NoticeReceived / sets LastNotice.
        bool noticeReceived = false;
        for (int i = 0; i < 30 && !noticeReceived; i++)
        {
            server.Poll();
            server.Tick(config.TickSeconds);
            wc.Poll();
            noticeReceived = wc.LastNotice?.Kind == ServerNoticeKind.Custom
                          && wc.LastNotice?.Message == "hello";
        }

        Assert.True(noticeReceived,
            $"Notice did not arrive within 30 ticks. LastNotice={wc.LastNotice?.Kind}/{wc.LastNotice?.Message}");

        await http.StopAsync();
    }

    /// <summary>
    /// GET /accounts (a read) must thread the request's cancellation token (RequestAborted) into
    /// ListAccountsAsync, so a long enumeration stops when the client disconnects. A real request token is
    /// cancellable; default(CancellationToken) is not, so CanBeCanceled proves the handler forwarded it.
    /// </summary>
    [Fact]
    public async Task GetAccounts_ThreadsRequestCancellationToken()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var store = new TokenCapturingAccountStore();
        var admin = new ServerAdmin(server, bans: null, accounts: store);

        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        HttpResponseMessage resp = await hc.GetAsync($"https://127.0.0.1:{port}/admin/accounts?prefix=player:");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True(store.Captured.CanBeCanceled,
            "GET /accounts should thread the request's RequestAborted token into ListAccountsAsync, not pass default.");

        await http.StopAsync();
    }

    // Records the cancellation token handed to EnumerateAsync so the test can confirm the HTTP layer forwarded the
    // request token (a cancellable one) rather than default.
    private sealed class TokenCapturingAccountStore : IEnumerableWorldStore
    {
        public CancellationToken Captured { get; private set; }

        public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
            string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Captured = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }
    }
}
