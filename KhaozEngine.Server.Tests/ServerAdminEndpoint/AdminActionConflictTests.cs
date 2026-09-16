using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

/// <summary>
/// The two outcomes the content catalog's status codes need and the admin result could not express: a 409
/// carrying a body, and a 400 carrying a DOCUMENT rather than one string. Both are asserted over a real
/// loopback HTTPS listener, because the mapping under test is the dispatch switch rather than the factory.
/// <para>
/// The third fact here is the one that makes the other two safe to ship: every existing caller builds a
/// rejection from the STRING overload, so its body stays exactly <c>{ "error": "..." }</c> with no second
/// property and no reshaping.
/// </para>
/// </summary>
public class AdminActionConflictTests
{
    private sealed class NullAdminControllable : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) { }
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }
    }

    // Owns the started endpoint plus its HTTPS client so each test wraps its setup in one `await using`.
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

    // A stuck peer is the failure this budget exists for: without it HttpClient waits its 100 second default.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static async Task<Harness> StartAsync(ServerAdmin admin)
    {
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
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        return new Harness
        {
            Http = http,
            Client = hc,
            Handler = handler,
            BaseUrl = $"https://127.0.0.1:{http.BoundPort}/admin",
        };
    }

    /// <summary>
    /// A conflict dispatches to 409 with its payload as the body. The publish action's optimistic concurrency
    /// is what needs it: a second console publishing a draft whose base moved has to be told BOTH numbers, and
    /// under the old three-value status that answer was a 500 with no body at all.
    /// </summary>
    [Fact]
    public async Task Conflict_MapsTo409_WithThePayloadAsTheBody()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("publish", _ => AdminActionResult.Conflict(new
        {
            error = "base version moved",
            expectedBaseVersion = 47,
            actualBaseVersion = 48,
        }));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.PostAsync(h.BaseUrl + "/actions/publish",
            new StringContent("{\"expectedBaseVersion\":47}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("base version moved", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(47, doc.RootElement.GetProperty("expectedBaseVersion").GetInt32());
        Assert.Equal(48, doc.RootElement.GetProperty("actualBaseVersion").GetInt32());
    }

    /// <summary>
    /// A rejection carrying a DOCUMENT dispatches to 400 with that document as the body. The validator
    /// accumulates every finding rather than stopping at the first, so the shape an operator needs is an
    /// array, and the string overload could only ever carry one of them flattened into a sentence.
    /// </summary>
    [Fact]
    public async Task BadRequestWithAPayload_MapsTo400_WithThePayloadAsTheBody()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("validate", _ => AdminActionResult.BadRequest(new
        {
            error = "content edit refused",
            findings = new[]
            {
                new { code = "KEC0004", type = "item", id = 13, message = "no field 'valeu' on type 'item'" },
                new { code = "KEC0005", type = "item", id = 61, message = "required field 'tradable' is absent" },
            },
        }));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/validate");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("content edit refused", doc.RootElement.GetProperty("error").GetString());
        JsonElement findings = doc.RootElement.GetProperty("findings");
        Assert.Equal(2, findings.GetArrayLength());
        Assert.Equal("KEC0004", findings[0].GetProperty("code").GetString());
        Assert.Equal(13, findings[0].GetProperty("id").GetInt32());
        Assert.Equal("KEC0005", findings[1].GetProperty("code").GetString());
    }

    /// <summary>
    /// The STRING overload keeps its exact current shape, which is the whole reason the object one is an
    /// overload rather than a replacement: every rejection in the engine and in four games builds one, and a
    /// body that gained or lost a property would break each of them at once.
    /// </summary>
    [Fact]
    public async Task BadRequestWithAString_KeepsItsExactCurrentShape()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("legacy", _ => AdminActionResult.BadRequest("timeOfDay (float) required"));
        await using Harness h = await StartAsync(admin);

        HttpResponseMessage resp = await h.Client.GetAsync(h.BaseUrl + "/actions/legacy");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string[] properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal("timeOfDay (float) required", doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The string overload still carries its message on <c>Error</c> and nothing on <c>Payload</c>, and the
    /// object one is the mirror image. A handler that read <c>Error</c> to log a rejection keeps reading it.
    /// </summary>
    [Fact]
    public void TheTwoRejectionOverloads_CarryTheirValueInDifferentPlaces()
    {
        AdminActionResult fromString = AdminActionResult.BadRequest("nope");
        Assert.Equal(AdminActionStatus.BadRequest, fromString.Status);
        Assert.Equal("nope", fromString.Error);
        Assert.Null(fromString.Payload);

        var document = new { error = "nope", findings = Array.Empty<string>() };
        AdminActionResult fromObject = AdminActionResult.BadRequest(document);
        Assert.Equal(AdminActionStatus.BadRequest, fromObject.Status);
        Assert.Null(fromObject.Error);
        Assert.Same(document, fromObject.Payload);

        AdminActionResult conflict = AdminActionResult.Conflict(document);
        Assert.Equal(AdminActionStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Error);
        Assert.Same(document, conflict.Payload);
    }

    /// <summary>
    /// Neither object factory takes a null. A 409 with an empty body is the answer the change exists to stop
    /// shipping, so the refusal is at the factory where the caller can see it rather than at the dispatch.
    /// </summary>
    [Fact]
    public void TheObjectFactories_RefuseANullPayload()
    {
        Assert.Throws<ArgumentNullException>(() => AdminActionResult.Conflict(null!));
        Assert.Throws<ArgumentNullException>(() => AdminActionResult.BadRequest((object)null!));
    }
}
