using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Published diffs and rollback rendering read whole versions without repeating pages.</summary>
public sealed class CatalogVersionRowsActionTests
{
    [Fact]
    public async Task InMemory_PublishedDiffAndRollback_ReadWholeVersions()
    {
        using var fixture = new CatalogVersionRowsFixture();
        var store = new InMemoryContentAuthoringStore(fixture.Registry, fixture.Pack);
        await AssertScenarioAsync(fixture, store);
    }

    [Fact]
    public async Task Sqlite_PublishedDiffAndRollback_ReadWholeVersions()
    {
        using var fixture = new CatalogVersionRowsFixture();
        using var store = new SqliteContentAuthoringStore("Data Source=:memory:", fixture.Registry, fixture.Pack);
        await AssertScenarioAsync(fixture, store);
    }

    [Fact]
    public async Task LegacyStore_PublishedDiff_PagesBeyond500Rows()
    {
        using var fixture = new CatalogVersionRowsFixture();
        var inner = new InMemoryContentAuthoringStore(fixture.Registry, fixture.Pack);
        await fixture.SeedAsync(inner);
        IContentAuthoringStore store = CatalogPagedRowStore.Wrap<CatalogPagedRowStore>(inner, out var counter);
        ServerAdmin admin = Surface(store, fixture);

        JsonElement diff = await OkAsync(admin, "catalog-diff", """{ "from": 1, "to": 2 }""");

        AssertInitialDiff(diff);
        Assert.Equal(new[] { (1, 0), (1, 500), (1, 0), (1, 500), (2, 0), (2, 500), (2, 0), (2, 500) }, counter.Pages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diff_FiltersToTheAdminRegistry(bool bulk)
    {
        using var fixture = new CatalogVersionRowsFixture();
        var inner = new InMemoryContentAuthoringStore(fixture.Registry, fixture.Pack);
        await fixture.SeedAsync(inner);
        IContentAuthoringStore store = bulk
            ? CatalogPagedRowStore.Wrap<CatalogBulkRowStore>(inner, out _)
            : CatalogPagedRowStore.Wrap<CatalogPagedRowStore>(inner, out _);
        var counter = (CatalogPagedRowStore)store;
        ContentTypeRegistry registry = CatalogFixtures.Registry(CatalogFixtures.ThingSpec);
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, store, registry);

        JsonElement diff = await OkAsync(admin, "catalog-diff", """{ "from": 1, "to": 2 }""");

        Assert.Equal(new[] { ("thing", 501, "update"), ("thing", 502, "add") }, Changes(diff));
        Assert.Equal("thing", Assert.Single(diff.GetProperty("chunkSummary").EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal(bulk ? 0 : 4, counter.Pages.Count);
    }

    internal static async Task AssertScenarioAsync(CatalogVersionRowsFixture fixture, IContentAuthoringStore inner)
    {
        await fixture.SeedAsync(inner);
        await CatalogVersionRowsReadAssertions.SeededAsync(inner);
        IContentAuthoringStore store = CatalogPagedRowStore.Wrap<CatalogBulkRowStore>(inner, out var counter);
        ServerAdmin admin = Surface(store, fixture);

        JsonElement initial = await OkAsync(admin, "catalog-diff", """{ "from": 1, "to": 2 }""");
        AssertInitialDiff(initial);
        Assert.Empty(counter.Pages);
        Assert.Equal(new[] { 1, 2 }, counter.Versions);

        JsonElement rollback = await OkAsync(admin, "catalog-rollback", """{ "toVersion": 1 }""");
        Assert.True(rollback.GetProperty("draftCreated").GetBoolean());
        Assert.Equal(2, rollback.GetProperty("editCount").GetInt32());
        ContentDraft draft = (await inner.GetOpenDraftAsync())!;
        Assert.Equal(new[] { (CatalogFixtures.ThingTypeId, 501), (CatalogFixtures.OtherTypeId, 500) },
            draft.Changes.Edits.Select(edit => (edit.Type.Value, edit.DefinitionId)));
        Assert.Equal(new long[] { 501, 500 }, draft.Changes.Edits.Select(edit => Assert.Single(edit.Fields).Value.Number));
        await inner.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator);
        await CatalogVersionRowsFixture.RetireAsync(inner);
        await CatalogVersionRowsReadAssertions.RetiredAsync(inner);

        counter.Pages.Clear();
        counter.Versions.Clear();
        JsonElement retired = await OkAsync(admin, "catalog-diff", """{ "from": 1, "to": 3 }""");
        Assert.Equal(new[] { ("thing", 500, "retire"), ("thing", 501, "update"), ("thing", 502, "add"),
            ("other_thing", 500, "update"), ("other_thing", 501, "retire") }, Changes(retired));
        Assert.Empty(counter.Pages);
        Assert.Equal(new[] { 1, 3 }, counter.Versions);

        counter.Versions.Clear();
        AdminActionResult blocked = await CatalogActionHarness.DispatchAsync(admin, "catalog-rollback", """{ "toVersion": 1 }""");
        Assert.Equal(AdminActionStatus.Conflict, blocked.Status);
        JsonElement body = CatalogActionHarness.Wire(blocked);
        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, body.GetProperty("reason").GetString());
        Assert.Equal(ContentRollback.BlockedCode, body.GetProperty("code").GetString());
        Assert.Equal(ContentRollback.Remedy, body.GetProperty("remedy").GetString());
        Assert.Equal(new[] { ("thing", 500, 2, 3, "Retired"), ("other_thing", 501, 1, 3, "Retired") },
            body.GetProperty("blockedByRules").EnumerateArray().Select(rule => (
                rule.GetProperty("type").GetString()!, rule.GetProperty("fromId").GetInt32(),
                rule.GetProperty("sequence").GetInt32(), rule.GetProperty("introducedIn").GetInt32(),
                rule.GetProperty("kind").GetString()!)));
        Assert.Null(await inner.GetOpenDraftAsync());
        Assert.Empty(counter.Pages);
        Assert.Equal(new[] { 1 }, counter.Versions);
    }

    static void AssertInitialDiff(JsonElement diff)
    {
        Assert.False(diff.GetProperty("provisionalIds").GetBoolean());
        Assert.Equal(new[] { ("thing", 501, "update"), ("thing", 502, "add"), ("other_thing", 500, "update") }, Changes(diff));
        JsonElement[] entries = diff.GetProperty("changes").EnumerateArray().ToArray();
        AssertField(entries[0], "501", "9001");
        AssertField(entries[1], null, "9003");
        AssertField(entries[2], "500", "9002");
        Assert.Equal(new[] { ("thing", 1, 2), ("other_thing", 1, 2) },
            diff.GetProperty("chunkSummary").EnumerateArray().Select(summary => (
                summary.GetProperty("type").GetString()!, summary.GetProperty("changedChunks").GetInt32(),
                summary.GetProperty("totalChunks").GetInt32())));
    }

    static void AssertField(JsonElement entry, string? before, string after)
    {
        JsonElement field = Assert.Single(entry.GetProperty("fields").EnumerateArray());
        Assert.Equal("value", field.GetProperty("field").GetString());
        Assert.Equal(before, field.GetProperty("before").GetString());
        Assert.Equal(after, field.GetProperty("after").GetString());
    }

    static (string Type, int Id, string Operation)[] Changes(JsonElement diff)
        => diff.GetProperty("changes").EnumerateArray().Select(change => (
            change.GetProperty("type").GetString()!, change.GetProperty("id").GetInt32(),
            change.GetProperty("op").GetString()!)).ToArray();

    static ServerAdmin Surface(IContentAuthoringStore store, CatalogVersionRowsFixture fixture)
    {
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, store, fixture.Registry);
        return admin;
    }

    static async Task<JsonElement> OkAsync(ServerAdmin admin, string action, string body)
    {
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, action, body);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        return CatalogActionHarness.Wire(result);
    }
}
