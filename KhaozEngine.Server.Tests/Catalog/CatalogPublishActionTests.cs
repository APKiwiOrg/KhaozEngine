using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The publish trio of spec 10.6 plus the version line of 10.7 and 10.8: <c>catalog-validate</c>,
/// <c>catalog-diff</c>, <c>catalog-publish</c>, <c>catalog-pin</c> and <c>catalog-rollback</c>.
/// <para>
/// The milestone gate is here: a publish whose <c>expectedBaseVersion</c> is stale answers a real 409
/// carrying BOTH numbers over the loopback HTTPS listener, because the mapping under test is the dispatch
/// switch rather than the factory that built the result.
/// </para>
/// </summary>
public sealed class CatalogPublishActionTests : IDisposable
{
    const string Token = "secret";

    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    readonly CatalogActionHarness _harness = new();

    /// <summary>
    /// The dry run builds the candidate and runs the full sweep WITHOUT allocating an id and without
    /// writing a version or a row. It is the same validator a publish runs, so a green validate followed by
    /// a red publish can only mean the draft changed in between.
    /// </summary>
    [Fact]
    public async Task Validate_OnACleanDraft_IsGreenAndAllocatesNothing()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);

        JsonElement body = await _harness.OkAsync("catalog-validate", null);

        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Equal(0, body.GetProperty("findingCount").GetInt32());
        Assert.Equal(published, body.GetProperty("baseVersion").GetInt32());
        Assert.Equal(published + 1, body.GetProperty("candidateVersion").GetInt32());

        // Nothing was allocated: the active version has not moved and the draft is still pending.
        Assert.Equal(published, await _harness.Store.GetActiveVersionAsync());
        Assert.Equal(1, (await _harness.Store.GetOpenDraftAsync())!.EditCount);
    }

    /// <summary>
    /// The dry run's baseline read performs the same STALE FREEZE recovery a publish's does, which is the one
    /// thing it is not silent about. <c>ContentDraftCandidate</c> itself writes nothing, but the baseline the
    /// action hands it comes from <c>ReadPublishBaselineAsync</c>, whose contract clears a marker naming a
    /// base version the store no longer stands at. That is a recovery rather than a side effect (only a
    /// publish that died between its commit and its own cleanup leaves one, and the draft it names would
    /// otherwise refuse every edit forever), and "writes nothing" was too strong a sentence for it.
    /// </summary>
    [Fact]
    public async Task Validate_ClearsAStaleFreezeMarkerTheSameWayAPublishDoes()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);

        // A marker naming a base version the store no longer stands at, which is what a publish that died
        // after its commit leaves behind. Every edit against the draft refuses while it is there.
        await _harness.Store.FreezeDraftAsync(published - 1);
        Assert.True((await _harness.Store.GetOpenDraftAsync())!.IsFrozen);

        await _harness.OkAsync("catalog-validate", null);

        Assert.False((await _harness.Store.GetOpenDraftAsync())!.IsFrozen);
    }

    /// <summary>
    /// An invalid candidate comes back with EVERY finding rather than the first, so an operator fixes three
    /// problems in one round trip instead of three.
    /// </summary>
    [Fact]
    public async Task Validate_CarriesEveryFindingInOneRoundTrip()
    {
        await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [
            { "op": "add", "typeKey": "thing", "key": "iron_sword", "fields": { "value": 12000 } },
            { "op": "add", "typeKey": "thing", "key": "steel_sword", "fields": { "value": 14000 } } ] }
        """);

        JsonElement body = await _harness.OkAsync("catalog-validate", null);

        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Equal(2, body.GetProperty("findingCount").GetInt32());
        Assert.Equal(new[] { "KEC0005", "KEC0005" }, CatalogActionHarness.Codes(body));
        Assert.Contains("stackable", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
    }

    /// <summary>
    /// The diff is FIELD LEVEL and carries a chunk summary, which is the operator-facing half of the
    /// one-item-edit budget: an operator sees the download cost of an edit before publishing it.
    /// </summary>
    [Fact]
    public async Task Diff_IsFieldLevelAndCarriesTheChunkSummary()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);

        JsonElement body = await _harness.OkAsync("catalog-diff", """{ "from": 0, "to": 0 }""");

        Assert.Equal(published, body.GetProperty("from").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("to").ValueKind);
        Assert.True(body.GetProperty("provisionalIds").GetBoolean());

        JsonElement change = body.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal("thing", change.GetProperty("type").GetString());
        Assert.Equal("update", change.GetProperty("op").GetString());
        Assert.Equal("stone_sword", change.GetProperty("key").GetString());
        JsonElement field = change.GetProperty("fields").EnumerateArray().Single();
        Assert.Equal(CatalogActionHarness.ValueField, field.GetProperty("field").GetString());
        Assert.Equal("4200", field.GetProperty("before").GetString());
        Assert.Equal("4500", field.GetProperty("after").GetString());

        JsonElement summary = body.GetProperty("chunkSummary").EnumerateArray()
            .Single(entry => entry.GetProperty("type").GetString() == "thing");
        Assert.Equal(1, summary.GetProperty("changedChunks").GetInt32());
        Assert.Equal(1, summary.GetProperty("totalChunks").GetInt32());
    }

    /// <summary>A diff between two PUBLISHED versions carries real ids, so it says so.</summary>
    [Fact]
    public async Task Diff_BetweenTwoPublishedVersions_CarriesNoProvisionalIds()
    {
        int first = await _harness.PublishThingsAsync("stone_sword");
        int second = await _harness.PublishThingsAsync("iron_sword");

        JsonElement body = await _harness.OkAsync("catalog-diff", $$"""
        { "from": {{first}}, "to": {{second}} }
        """);

        Assert.Equal(second, body.GetProperty("to").GetInt32());
        Assert.False(body.GetProperty("provisionalIds").GetBoolean());
        JsonElement change = body.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal("add", change.GetProperty("op").GetString());
        Assert.Equal("iron_sword", change.GetProperty("key").GetString());
    }

    /// <summary>
    /// A diff endpoint that names a version the store does not hold is a 400, not a 200 with no changes.
    /// <para>
    /// An empty change set is the answer to "these two versions are the same", and answering it to a typo
    /// tells an operator their edit is already published when nothing of the sort is true. The other three
    /// actions that take a version number all refuse an unknown one, so this one does too, under the same
    /// reason token.
    /// </para>
    /// </summary>
    /// <param name="from">The source version.</param>
    /// <param name="to">The destination version.</param>
    [Theory]
    [InlineData(1, 999)]
    [InlineData(999, 1)]
    [InlineData(998, 999)]
    public async Task Diff_NamingAVersionThatDoesNotExist_IsRefused(int from, int to)
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-diff", $$"""
        { "from": {{from}}, "to": {{to}} }
        """);

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, body.GetProperty("reason").GetString());
    }

    /// <summary>A publish reports the version it wrote and the work it cost.</summary>
    [Fact]
    public async Task Publish_ReportsTheNewVersionAndWhatItCost()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);

        JsonElement body = await _harness.OkAsync("catalog-publish", $$"""
        { "operator": "oid:8f2c", "note": "autumn price pass", "expectedBaseVersion": {{published}} }
        """);

        Assert.Equal(published + 1, body.GetProperty("version").GetInt32());
        Assert.Equal(64, body.GetProperty("serverManifestHash").GetString()!.Length);
        Assert.Equal(64, body.GetProperty("clientManifestHash").GetString()!.Length);
        Assert.Equal(ContentPackFormat.Generation, body.GetProperty("formatGeneration").GetInt32());
        Assert.True(body.GetProperty("chunksWritten").GetInt32() >= 1);
        Assert.True(body.GetProperty("bytesWritten").GetInt64() > 0);
        Assert.True(body.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// <c>expectedBaseVersion</c> is REQUIRED optimistic concurrency, so a publish that names none is a 400
    /// rather than a publish against whatever the store happens to stand at.
    /// </summary>
    [Fact]
    public async Task Publish_WithNoExpectedBaseVersion_IsRefused()
    {
        JsonElement body = await _harness.RefusedAsync("catalog-publish", """{ "operator": "oid:8f2c" }""");

        Assert.Contains("expectedBaseVersion", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stale expectation is a 409 naming BOTH numbers. Two consoles cannot both publish the same draft:
    /// the second one's expectation is stale, and a caller told what it expected and what the store stands
    /// at re-reads and retries rather than guessing.
    /// </summary>
    [Fact]
    public async Task Publish_WithAStaleExpectation_IsA409NamingBothNumbers()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);

        JsonElement body = await _harness.ConflictAsync("catalog-publish", $$"""
        { "operator": "oid:8f2c", "expectedBaseVersion": {{published - 1}} }
        """);

        Assert.Equal("base version moved", body.GetProperty("error").GetString());
        Assert.Equal(published - 1, body.GetProperty("expectedBaseVersion").GetInt32());
        Assert.Equal(published, body.GetProperty("actualBaseVersion").GetInt32());
    }

    /// <summary>
    /// THE MILESTONE GATE. The same refusal over the real loopback HTTPS listener, because the mapping
    /// under test is the dispatch switch rather than the factory: every 409 in this spec was a 500 with no
    /// body at all before the result type carried one.
    /// </summary>
    [Fact]
    public async Task Publish_OverTheLoopbackListener_IsARealHttp409CarryingBothNumbers()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword",
                       "fields": { "value": 12000, "stackable": false } } ] }
        """);

        var options = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = Token,
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(_harness.Admin, options);
        await http.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        string url = FormattableString.Invariant($"https://127.0.0.1:{http.BoundPort}/admin/actions/catalog-publish");
        HttpResponseMessage stale = await client.PostAsync(url, Json($$"""
        { "operator": "oid:8f2c", "expectedBaseVersion": {{published - 1}} }
        """));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using JsonDocument refused = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("base version moved", refused.RootElement.GetProperty("error").GetString());
        Assert.Equal(published - 1, refused.RootElement.GetProperty("expectedBaseVersion").GetInt32());
        Assert.Equal(published, refused.RootElement.GetProperty("actualBaseVersion").GetInt32());

        // The same draft under the RIGHT expectation publishes, which is what makes the 409 a race rather
        // than a wall: a caller re-reads the number and retries.
        HttpResponseMessage fresh = await client.PostAsync(url, Json($$"""
        { "operator": "oid:8f2c", "expectedBaseVersion": {{published}} }
        """));

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using JsonDocument ok = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        Assert.Equal(published + 1, ok.RootElement.GetProperty("version").GetInt32());

        await http.StopAsync();
    }

    /// <summary>
    /// A publish the validator refuses is a 400 carrying every finding, and NOTHING is written. There is
    /// deliberately no override flag: a publish that bypassed validation would make boot the only real
    /// gate, and boot fails closed.
    /// </summary>
    [Fact]
    public async Task Publish_RefusedByTheValidator_Carries_TheFindingsAndWritesNothing()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "edits": [ { "op": "add", "typeKey": "thing", "key": "iron_sword", "fields": { "value": 12000 } } ] }
        """);

        JsonElement body = await _harness.RefusedAsync("catalog-publish", $$"""
        { "operator": "oid:8f2c", "expectedBaseVersion": {{published}} }
        """);

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, body.GetProperty("reason").GetString());
        Assert.Equal(new[] { "KEC0005" }, CatalogActionHarness.Codes(body));
        Assert.Equal(published, await _harness.Store.GetActiveVersionAsync());
        Assert.NotNull(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>A pin naming a version that does not exist is a 400 rather than a hold on nothing.</summary>
    [Fact]
    public async Task Pin_NamingAVersionThatDoesNotExist_IsRefused()
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-pin", """{ "version": 99 }""");

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, body.GetProperty("reason").GetString());
        Assert.Null(await _harness.Store.GetPinnedVersionAsync());
    }

    /// <summary>A pin writes the hold, and a null version clears it, which is how a server catches up.</summary>
    [Fact]
    public async Task Pin_WritesTheHoldAndAnExplicitNullClearsIt()
    {
        int first = await _harness.PublishThingsAsync("stone_sword");
        await _harness.PublishThingsAsync("iron_sword");

        JsonElement held = await _harness.OkAsync("catalog-pin", $$"""
        { "operator": "oid:8f2c", "version": {{first}} }
        """);
        Assert.Equal(first, held.GetProperty("pinnedVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, held.GetProperty("configPinnedVersion").ValueKind);
        Assert.Empty(held.GetProperty("warnings").EnumerateArray());
        Assert.Equal(first, await _harness.Store.GetPinnedVersionAsync());

        JsonElement cleared = await _harness.OkAsync("catalog-pin", """{ "version": null }""");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("pinnedVersion").ValueKind);
        Assert.Null(await _harness.Store.GetPinnedVersionAsync());
    }

    /// <summary>A pin naming neither a version nor null is refused, because absent and null mean different things.</summary>
    [Fact]
    public async Task Pin_NamingNeitherAVersionNorNull_IsRefused()
    {
        JsonElement body = await _harness.RefusedAsync("catalog-pin", """{ "operator": "oid:8f2c" }""");

        Assert.Contains("version", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pin against a server whose own CONFIG pins a version returns 200 carrying
    /// <c>configPinnedVersion</c> and a warning naming it. The write happened and takes effect the moment
    /// the config pin is removed, and a bare 200 for a call with no effect on the next restart is the
    /// failure this answer exists to prevent.
    /// </summary>
    [Fact]
    public async Task Pin_AgainstAConfigPinnedServer_Says_ConfigWins()
    {
        using var pinned = new CatalogActionHarness(new CatalogAdminActionOptions { ConfiguredVersion = 52 });
        int first = await pinned.PublishThingsAsync("stone_sword");

        JsonElement body = await pinned.OkAsync("catalog-pin", $$"""{ "version": {{first}} }""");

        Assert.Equal(first, body.GetProperty("pinnedVersion").GetInt32());
        Assert.Equal(52, body.GetProperty("configPinnedVersion").GetInt32());
        string warning = body.GetProperty("warnings").EnumerateArray().Single().GetString()!;
        Assert.Contains("52", warning, StringComparison.Ordinal);
        Assert.Equal(first, await pinned.Store.GetPinnedVersionAsync());
    }

    /// <summary>
    /// A pin naming a version whose minimum server build exceeds the running build is ACCEPTED with a
    /// warning, because the operator may be pinning ahead of an upgrade on purpose and the boot check is
    /// the real gate.
    /// </summary>
    [Fact]
    public async Task Pin_AheadOfTheRunningBuild_IsAcceptedWithAWarning()
    {
        using var old = new CatalogActionHarness(new CatalogAdminActionOptions { ServerBuild = 4000 });
        await old.Store.ApplyEditsAsync(
            [ContentEdit.Add(CatalogActionHarness.Thing, new ContentKey("stone_sword"), CatalogActionHarness.Fields())],
            CatalogAdminActions.Actor,
            CatalogActionHarness.Operator,
            "fixture");
        ContentPublishResult published = await old.Store.PublishAsync(
            new ContentPublishRequest(CatalogAdminActions.Actor, CatalogActionHarness.Operator, "fixture", 0, 4120, 4118));

        JsonElement body = await old.OkAsync("catalog-pin", $$"""{ "version": {{published.VersionNumber}} }""");

        Assert.Equal(published.VersionNumber, body.GetProperty("pinnedVersion").GetInt32());
        string warning = body.GetProperty("warnings").EnumerateArray().Single().GetString()!;
        Assert.Contains("4120", warning, StringComparison.Ordinal);
        Assert.Equal(published.VersionNumber, await old.Store.GetPinnedVersionAsync());
    }

    /// <summary>
    /// A rollback BUILDS A DRAFT and publishes nothing, so the operator reviews the diff and publishes it.
    /// That is what makes a rollback reviewable rather than a second uncontrolled change.
    /// </summary>
    [Fact]
    public async Task Rollback_BuildsADraftRatherThanPublishingOne()
    {
        int first = await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 9900 } } ] }
        """);
        await _harness.OkAsync("catalog-publish", $$"""{ "expectedBaseVersion": {{first}} }""");

        JsonElement body = await _harness.OkAsync("catalog-rollback", $$"""
        { "operator": "oid:8f2c", "toVersion": {{first}}, "note": "revert the autumn pass" }
        """);

        Assert.True(body.GetProperty("draftCreated").GetBoolean());
        Assert.Equal(1, body.GetProperty("editCount").GetInt32());
        Assert.Empty(body.GetProperty("blockedByRules").EnumerateArray());
        Assert.Equal(first + 1, await _harness.Store.GetActiveVersionAsync());
    }

    /// <summary>
    /// A rollback blocked by an irreversible retire is a 409 carrying <c>KEC0039</c>, the blocking rules and
    /// a remedy naming the mint-a-new-id path, so the operator is not left guessing.
    /// </summary>
    [Fact]
    public async Task Rollback_BlockedByARetire_Is409WithTheRulesAndTheRemedy()
    {
        int first = await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "retire", "typeKey": "thing", "id": {{sword}}, "policy": "placeholder" } ] }
        """);
        await _harness.OkAsync("catalog-publish", $$"""{ "expectedBaseVersion": {{first}} }""");

        JsonElement body = await _harness.ConflictAsync("catalog-rollback", $$"""
        { "operator": "oid:8f2c", "toVersion": {{first}} }
        """);

        Assert.Equal("KEC0039", body.GetProperty("code").GetString());
        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, body.GetProperty("reason").GetString());
        JsonElement rule = body.GetProperty("blockedByRules").EnumerateArray().Single();
        Assert.Equal(1, rule.GetProperty("sequence").GetInt32());
        Assert.Equal(first + 1, rule.GetProperty("introducedIn").GetInt32());
        Assert.Equal("thing", rule.GetProperty("type").GetString());
        Assert.Equal(sword, rule.GetProperty("fromId").GetInt32());
        Assert.Equal("Retired", rule.GetProperty("kind").GetString());
        Assert.Contains("new key", body.GetProperty("remedy").GetString()!, StringComparison.Ordinal);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>A rollback naming a version the store does not hold is a 400 rather than a throw.</summary>
    [Fact]
    public async Task Rollback_NamingAVersionThatDoesNotExist_IsRefused()
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-rollback", """{ "toVersion": 99 }""");

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, body.GetProperty("reason").GetString());
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
