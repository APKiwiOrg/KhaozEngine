using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Server.Admin;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Catalog string decoding refusals through the real HTTPS action boundary.</summary>
public sealed class CatalogStringRequestTests : IDisposable
{
    readonly CatalogActionHarness _harness = new();

    // #1255: a lone surrogate must be a request refusal, while a complete pair still reads.
    [Theory]
    [InlineData("catalog-edit", "operator")]
    [InlineData("catalog-edit", "note")]
    [InlineData("catalog-publish", "operator")]
    [InlineData("catalog-publish", "note")]
    [InlineData("catalog-rollback", "operator")]
    [InlineData("catalog-rollback", "note")]
    [InlineData("catalog-pin", "operator")]
    [InlineData("catalog-import", "operator")]
    [InlineData("catalog-import", "note")]
    [InlineData("catalog-discard", "operator")]
    [InlineData("catalog-sweep", "operator")]
    public async Task UnreadableRequestString_IsMalformedAndACompletePairIsAccepted(string action, string member)
    {
        string request = await PrepareAsync(action);
        string before = await _harness.SnapshotAsync();

        JsonElement refused = await PostAsync(
            action, WithMember(request, member, @"\ud800"), HttpStatusCode.BadRequest);

        Assert.Equal(CatalogRequest.MalformedRequestReason, refused.GetProperty("reason").GetString());
        Assert.Empty(refused.GetProperty("findings").EnumerateArray());
        Assert.Equal(before, await _harness.SnapshotAsync());

        await PostAsync(action, WithMember(request, member, @"\ud83d\ude00"), HttpStatusCode.OK);
    }

    // #1255: each edit reader retains its existing finding family, including opaque hex values.
    [Theory]
    [InlineData("""{ "op": "\ud800", "typeKey": "thing", "key": "new_sword" }""", "KEC0004")]
    [InlineData("""{ "op": "add", "typeKey": "\ud800", "key": "new_sword" }""", "KEC0004")]
    [InlineData("""{ "op": "add", "typeKey": "thing", "key": "\ud800" }""", "KEC0004")]
    [InlineData("""{ "op": "add", "typeKey": "thing", "key": "new_sword", "family": "\ud800" }""", "KEC0004")]
    [InlineData("""{ "op": "retire", "typeKey": "thing", "key": "stone_sword", "policy": "\ud800" }""", "KEC0004")]
    [InlineData("""{ "op": "retire", "typeKey": "thing", "key": "stone_sword", "policy": "replacement", "replacementKey": "\ud800" }""", "KEC0004")]
    [InlineData("""{ "op": "fork", "typeKey": "thing", "key": "stone_sword", "forkKey": "\ud800", "flagField": "legacy" }""", "KEC0041")]
    [InlineData("""{ "op": "fork", "typeKey": "thing", "key": "stone_sword", "forkKey": "sword_legacy", "flagField": "\ud800" }""", "KEC0041")]
    [InlineData("""{ "op": "add", "typeKey": "thing", "key": "new_sword", "fields": { "value": 4200, "stackable": false, "icon": "\ud800" } }""", "KEC0004")]
    [InlineData("""{ "op": "add", "typeKey": "thing", "key": "new_sword", "fields": { "value": 4200, "stackable": false, "icon": "\udc00" } }""", "KEC0004")]
    public async Task UnreadableEditString_IsAContentFindingAndAppliesNothing(string entry, string code)
    {
        await _harness.PublishThingsAsync("stone_sword");
        string before = await _harness.SnapshotAsync();

        JsonElement refused = await PostAsync(
            "catalog-edit", "{ \"edits\": [" + entry + "] }", HttpStatusCode.BadRequest);

        Assert.Equal(CatalogEditParser.Reason, refused.GetProperty("reason").GetString());
        Assert.Equal(new[] { code }, CatalogActionHarness.Codes(refused));
        Assert.Equal(before, await _harness.SnapshotAsync());
    }

    // #1255: reads keep their documented error-only response rather than becoming server faults.
    [Theory]
    [InlineData("catalog-list", """{ "typeKey": "\ud800" }""")]
    [InlineData("catalog-get", """{ "typeKey": "\ud800", "key": "stone_sword" }""")]
    [InlineData("catalog-list", """{ "typeKey": "thing", "keyPrefix": "\udc00" }""")]
    [InlineData("catalog-get", """{ "typeKey": "thing", "key": "\ud800" }""")]
    public async Task UnreadableReadString_IsAnErrorOnlyBadRequest(string action, string request)
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement refused = await PostAsync(action, request, HttpStatusCode.BadRequest);

        Assert.False(string.IsNullOrWhiteSpace(refused.GetProperty("error").GetString()));
        Assert.Equal("error", Assert.Single(refused.EnumerateObject()).Name);
    }

    async Task<string> PrepareAsync(string action)
    {
        if (action == "catalog-import")
        {
            using var source = new CatalogActionHarness();
            await source.PublishThingsAsync("stone_sword");
            JsonElement exported = await source.OkAsync("catalog-export", null);
            return "{ \"bundle\": " + exported.GetProperty("bundle").GetRawText() + " }";
        }

        int version = await _harness.PublishThingsAsync("stone_sword");
        if (action is "catalog-publish" or "catalog-discard")
        {
            await _harness.Store.ApplyEditsAsync(
                [ContentEdit.Add(CatalogActionHarness.Thing, new ContentKey("new_sword"), CatalogActionHarness.Fields())],
                CatalogAdminActions.Actor, CatalogActionHarness.Operator, "pending");
        }
        if (action == "catalog-rollback")
        {
            await _harness.PublishThingsAsync("new_sword");
        }

        return action switch
        {
            "catalog-edit" => """{ "edits": [ { "op": "add", "typeKey": "thing", "key": "new_sword", "fields": { "value": 4200, "stackable": false } } ] }""",
            "catalog-publish" => FormattableString.Invariant($"{{ \"expectedBaseVersion\": {version} }}"),
            "catalog-rollback" => FormattableString.Invariant($"{{ \"toVersion\": {version} }}"),
            "catalog-pin" => """{ "version": null }""",
            _ => "{}",
        };
    }

    static string WithMember(string request, string member, string escapedValue)
    {
        string prefix = request[..^1].TrimEnd();
        string separator = prefix.EndsWith('{') ? string.Empty : ",";
        return prefix + separator + "\"" + member + "\":\"" + escapedValue + "\"}";
    }

    async Task<JsonElement> PostAsync(string action, string request, HttpStatusCode expected)
    {
        var certificate = AdminTlsCertificate.CreateSelfSigned("localhost");
        using var tls = certificate.Certificate;
        await using var http = new AdminHttpServer(_harness.Admin, new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "catalog-test",
            Certificate = certificate,
        });
        await http.StartAsync();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "catalog-test");
        string url = FormattableString.Invariant($"https://127.0.0.1:{http.BoundPort}/admin/actions/{action}");
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(url, content);

        Assert.Equal(expected, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
