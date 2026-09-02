using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using KhaozEngine.WorldStore;
using KhaozEngine.Tests.NetWorld;   // TestHandshake
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

public class AdminHttpServerTests
{
    // Port 0 throughout: Kestrel takes an OS-assigned port and AdminHttpServer.BoundPort reports it back, so there
    // is no probe-release-rebind window in which another listener on the host can take the port between the pick and
    // the bind, and no URL exists before the socket is accepting (#720).
    // The client budget is the other half: without it HttpClient waits its 100 second default, so a wrong listener
    // sitting on our port fails the run in minutes rather than seconds.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task Requires_Bearer_And_Serves_Online()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);

        var admin = new ServerAdmin(server);
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        string baseUrl = $"https://127.0.0.1:{http.BoundPort}/admin";

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
        var client = new NetClient(ct, TestHandshake.Wire("bob"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        Assert.Equal(1, server.PlayerCount);

        var admin = new ServerAdmin(server);

        // Teleport the connected player off the origin and tick so the authoritative move + published online snapshot
        // both carry the new position.
        var moved = new System.Numerics.Vector3(12.5f, 0f, -7.25f);
        admin.Teleport(PlayerRef.Slot(0), moved);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }

        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        HttpResponseMessage ok = await hc.GetAsync($"https://127.0.0.1:{http.BoundPort}/admin/online");
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
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        string baseUrl = $"https://127.0.0.1:{http.BoundPort}/admin";

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
    /// POST /ban on a TOKENLESS connection's id is the operator's mistake, not a server fault, so it comes back
    /// 400 with the reason rather than 500 with a stack trace. ServerAdmin refuses the id because it names a seat
    /// the allocator recycles, and an admin tool can only surface that if the endpoint keeps the message.
    /// </summary>
    [Fact]
    public async Task PostBan_OnAGuestAccount_IsABadRequestWithTheReason()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var bans = new InMemoryBanStore();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var admin = new ServerAdmin(server, bans);

        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        string seat = ResumePositionCache.GuestAccountPrefix + "0";
        using var body = new StringContent(
            $$"""{"accountId":"{{seat}}","reason":"griefing"}""", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await hc.PostAsync($"https://127.0.0.1:{http.BoundPort}/admin/ban", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("seat", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(admin.ListBans());

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

        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        HttpResponseMessage resp = await hc.GetAsync($"https://127.0.0.1:{http.BoundPort}/admin/accounts?prefix=player:");
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
