using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Checks public diffs after reopening a multi-type database with only one type registered.</summary>
internal static class CatalogVersionRowsPartialRegistryAssertions
{
    public static async Task ReopenedAsync(IContentAuthoringStore provider, ContentTypeRegistry registry)
    {
        await provider.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        IContentAuthoringStore legacy = CatalogPagedRowStore.Wrap<CatalogPagedRowStore>(provider, out var pages);
        JsonElementResult legacyDiff = await DiffAsync(legacy, registry);
        Assert.Equal(AdminActionStatus.Ok, legacyDiff.Status);
        Assert.Equal(new[] { (1, 0), (1, 500), (3, 0), (3, 500) }, pages.Pages);

        IContentAuthoringStore bulk = CatalogPagedRowStore.Wrap<CatalogBulkRowStore>(provider, out var counter);
        JsonElementResult diff = await DiffAsync(bulk, registry);
        Assert.Equal(AdminActionStatus.Ok, diff.Status);
        Assert.Equal(legacyDiff.Json, diff.Json);
        Assert.Empty(counter.Pages);
        Assert.Equal(new[] { 1, 3 }, counter.Versions);

        IContentVersionRowSource source = Assert.IsAssignableFrom<IContentVersionRowSource>(provider);
        var first = await source.ReadVersionRowsAsync(1);
        var active = await source.ReadVersionRowsAsync(0);
        Assert.Equal(501, first.Count);
        Assert.Equal(502, active.Count);
        Assert.Equal(Enumerable.Range(1, 501), first.Select(revision => revision.Row.Id));
        Assert.All(first, revision => Assert.Equal(CatalogFixtures.Thing, revision.Row.Type));
        Assert.All(active, revision => Assert.Equal(CatalogFixtures.Thing, revision.Row.Type));
        Assert.False(first[499].Row.IsRetired);
        Assert.Equal(3, first[499].ReplacedInVersion);
        Assert.True(active[499].Row.IsRetired);
        Assert.Equal(3, active[499].ValidFromVersion);
        Assert.Equal(9001, active[500].Row.Fields[0].Number);

        ContentAuthoringException strict = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => provider.ReadPublishBaselineAsync());
        Assert.Equal(ContentAuthoringException.UnknownTypeReason, strict.Reason);
    }

    static async Task<JsonElementResult> DiffAsync(IContentAuthoringStore store, ContentTypeRegistry registry)
    {
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, store, registry);
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(
            admin, "catalog-diff", """{ "from": 1, "to": 3 }""");
        var body = CatalogActionHarness.Wire(result);
        if (result.Status == AdminActionStatus.Ok)
        {
            Assert.Equal(new[] { ("thing", 500, "retire"), ("thing", 501, "update"), ("thing", 502, "add") },
                body.GetProperty("changes").EnumerateArray().Select(change => (
                    change.GetProperty("type").GetString()!, change.GetProperty("id").GetInt32(),
                    change.GetProperty("op").GetString()!)));
            Assert.Equal("thing", Assert.Single(body.GetProperty("chunkSummary").EnumerateArray())
                .GetProperty("type").GetString());
        }

        return new JsonElementResult(result.Status, body.GetRawText());
    }

    readonly record struct JsonElementResult(AdminActionStatus Status, string Json);
}
