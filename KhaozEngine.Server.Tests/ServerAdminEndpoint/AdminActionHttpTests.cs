using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using KhaozEngine.Tests.Catalog;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

/// <summary>
/// Exercises the game-registered admin action seam over a real loopback HTTPS listener, cloning the AdminHttpServer
/// harness (OS-assigned port, self-signed cert, bearer client). The action registry never touches the simulation, so
/// the backing world is a do-nothing controllable rather than a live server.
/// </summary>
public class AdminActionHttpTests
{

    private sealed class NullAdminControllable : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) { }
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }
    }

    // Owns the started endpoint plus its HTTPS client so each test can wrap the setup in a single `await using`.
    private sealed class Harness : IAsyncDisposable
    {
        public required AdminHttpServer Http { get; init; }
        public required HttpClient Client { get; init; }
        public required HttpClientHandler Handler { get; init; }
        public required string BaseUrl { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Http.StopAsync();
            await Http.DisposeAsync();
            Client.Dispose();
            Handler.Dispose();
        }
    }

    // A stuck peer is the failure this budget exists for: without it HttpClient waits its 100 second default, which
    // turns a wrong-listener-on-our-port into a three minute red instead of a quick one (#720).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static async Task<Harness> StartAsync(ServerAdmin admin, bool withBearer = true)
    {
        // Port 0: Kestrel takes an OS-assigned port and reports it back, so there is no probe-release-rebind window
        // in which another listener on the host can take the port between the pick and the bind (#720). BoundPort is
        // readable only after StartAsync, which is also when the socket is accepting, so the client cannot beat it.
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        if (withBearer) hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        return new Harness
        {
            Http = http,
            Client = hc,
            Handler = handler,
            BaseUrl = $"https://127.0.0.1:{http.BoundPort}/admin",
        };
    }

    /// <summary>
    /// Port 0 is the race-free way to reach an ephemeral port: Kestrel binds one itself and reports it back, so no
    /// probe socket has to release a port before the real bind and no second listener can slip into that window
    /// (#720). BoundPort staying unreadable until the bind has happened is the other half of the contract, since it
    /// is what stops a caller building a URL for a socket that is not accepting yet.
    /// </summary>
    [Fact]
    public async Task Port0_BindsAnEphemeralPort_ReadableOnlyOnceStarted()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("ping", _ => AdminActionResult.Ok());
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        Assert.Throws<InvalidOperationException>(() => _ = http.BoundPort);

        await http.StartAsync();
        Assert.InRange(http.BoundPort, 1, 65535);

        // The reported port is the one actually listening, not a number the caller picked.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage resp = await hc.GetAsync($"https://127.0.0.1:{http.BoundPort}/admin/actions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await http.StopAsync();
    }

    [Fact]
    public async Task ActionsList_RequiresBearer_AndReturnsNames()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("bravo", _ => AdminActionResult.Ok());
        admin.RegisterAction("alpha", _ => AdminActionResult.Ok());
        await using Harness h = await StartAsync(admin, withBearer: false);

        HttpResponseMessage noAuth = await h.Client.GetAsync(h.BaseUrl + "/actions");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage ok = await h.Client.GetAsync(h.BaseUrl + "/actions");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        string?[] names = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "bravo" }, names);   // sorted ordinal
    }

    [Fact]
    public async Task Post_DispatchesPayload_AndReturnsOkJson()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("echo", (JsonElement? payload, CancellationToken _) =>
            Task.FromResult(payload is { } p ? AdminActionResult.Ok(p) : AdminActionResult.Ok()));
        await using Harness h = await StartAsync(admin);

        var body = new StringContent("{\"msg\":\"hi\",\"n\":42}", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await h.Client.PostAsync(h.BaseUrl + "/actions/echo", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("hi", doc.RootElement.GetProperty("msg").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Get_DispatchesWithNullPayload()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("whoami", (JsonElement? payload, CancellationToken _) =>
            Task.FromResult(AdminActionResult.Ok(new { hadPayload = payload.HasValue })));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/whoami");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("hadPayload").GetBoolean());
    }

    [Fact]
    public async Task UnknownAction_Returns404()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BadRequest_MapsTo400_WithErrorBody()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("validate", _ => AdminActionResult.BadRequest("timeOfDay (float) required"));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/validate");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("timeOfDay (float) required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Accepted_MapsTo202()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("enqueue", _ => AdminActionResult.Accepted());
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.PostAsync(h.BaseUrl + "/actions/enqueue",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonBody_Returns400()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("echo", (JsonElement? payload, CancellationToken _) => Task.FromResult(AdminActionResult.Ok()));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.PostAsync(h.BaseUrl + "/actions/echo",
            new StringContent("{not valid json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("malformed json body", doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A body containing the literal JSON null parses to a JsonElement of ValueKind.Null (HasValue true), which
    /// would make the canonical handler idiom payload?.GetProperty(...) throw a 500. Dispatch must normalize it so
    /// absent, empty, whitespace-only, and JSON-null bodies all reach the handler as a null payload.
    /// </summary>
    [Fact]
    public async Task JsonNullBody_DispatchesWithNullPayload()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("whoami", (JsonElement? payload, CancellationToken _) =>
            Task.FromResult(AdminActionResult.Ok(new { hadPayload = payload.HasValue })));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.PostAsync(h.BaseUrl + "/actions/whoami",
            new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("hadPayload").GetBoolean());
    }

    /// <summary>
    /// Dispatch must hand the handler the request's RequestAborted token, mirroring GET /accounts. A real request
    /// token is cancellable, default(CancellationToken) is not, so CanBeCanceled proves it was forwarded.
    /// </summary>
    [Fact]
    public async Task Handler_ReceivesRequestAborted()
    {
        CancellationToken captured = default;
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("capture", (JsonElement? payload, CancellationToken ct) =>
        {
            captured = ct;
            return Task.FromResult(AdminActionResult.Ok());
        });
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/capture");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(captured.CanBeCanceled,
            "dispatch should hand the handler the request's RequestAborted token, not default.");
    }

    /// <summary>
    /// An action registered as MUTATING answers 405 to GET, and the read route is untouched.
    /// <para>
    /// The endpoint maps both verbs onto every registered name, so a destructive action with no required body
    /// was reachable by any GET: a browser address bar, a link preview, a crawler following the name out of
    /// <c>/actions</c>. The engine's own destructive routes have always been <c>MapPost</c> only, and a
    /// registered action is the one place that guarantee was missing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_OnAMutatingAction_Is405AndNamesThePostItAllows()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("wipe", _ => AdminActionResult.Ok(), mutating: true);
        admin.RegisterAction("peek", _ => AdminActionResult.Ok());
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage blocked = await h.Client.GetAsync(h.BaseUrl + "/actions/wipe");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, blocked.StatusCode);
        Assert.Contains("POST", blocked.Content.Headers.Allow);

        HttpResponseMessage posted = await h.Client.PostAsync(
            h.BaseUrl + "/actions/wipe", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        HttpResponseMessage read = await h.Client.GetAsync(h.BaseUrl + "/actions/peek");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// The real registration: the eleven catalog actions that write are POST only and the five reads take
    /// either verb. <c>catalog-discard</c> and <c>catalog-sweep</c> are the two that matter most, because
    /// both are destructive and neither needs a body, so a bare GET used to run them.
    /// </summary>
    /// <param name="action">The action name.</param>
    /// <param name="mutating">Whether the name is one of the eleven.</param>
    [Theory]
    [InlineData("catalog-discard", true)]
    [InlineData("catalog-sweep", true)]
    [InlineData("catalog-edit", true)]
    [InlineData("catalog-publish", true)]
    [InlineData("catalog-import", true)]
    [InlineData("catalog-list", false)]
    [InlineData("catalog-schema", false)]
    [InlineData("catalog-versions", false)]
    public async Task Get_OnTheCatalogActions_Is405ForTheElevenThatWrite(string action, bool mutating)
    {
        using var catalog = new CatalogActionHarness();
        await using Harness h = await StartAsync(catalog.Admin);

        HttpResponseMessage got = await h.Client.GetAsync(h.BaseUrl + "/actions/" + action);

        Assert.Equal(mutating, got.StatusCode == HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>
    /// A GET that would have discarded the open draft does not, and the POST that is meant to still does.
    /// The 405 has to be a REFUSAL rather than a different rendering of the same side effect.
    /// </summary>
    [Fact]
    public async Task Get_OnCatalogDiscard_LeavesTheDraftStanding()
    {
        using var catalog = new CatalogActionHarness();
        await catalog.PublishThingsAsync("stone_sword");
        await catalog.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);
        await using Harness h = await StartAsync(catalog.Admin);

        HttpResponseMessage blocked = await h.Client.GetAsync(h.BaseUrl + "/actions/catalog-discard");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, blocked.StatusCode);
        Assert.NotNull(await catalog.Store.GetOpenDraftAsync());

        HttpResponseMessage posted = await h.Client.PostAsync(
            h.BaseUrl + "/actions/catalog-discard", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Null(await catalog.Store.GetOpenDraftAsync());
    }
}
