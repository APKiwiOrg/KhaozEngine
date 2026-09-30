using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>The operator sweep must keep the version record's authority when it calls the shared sweep.</summary>
public sealed class CatalogSweepPointerTests
{
    [Fact]
    public async Task Sweep_WithAnotherCatalogsPointer_SkipsAndPreservesTheLiveClosure()
    {
        using var first = new CatalogActionHarness();
        using var second = new CatalogActionHarness();
        Assert.Equal(1, await first.PublishThingsAsync("stone_sword"));
        Assert.Equal(1, await second.PublishThingsAsync("iron_sword"));
        var live = new List<string>();
        await foreach (string hash in first.Pack.ListAsync(1))
        {
            live.Add(hash);
        }

        // Both complete packs exist, so the stale pointer produces a valid listing of the wrong closure.
        await foreach (string hash in second.Pack.EnumerateAsync())
        {
            var bytes = await second.Pack.GetAsync(hash);
            Assert.NotNull(bytes);
            await first.Pack.PutAsync(hash, bytes.Value);
        }

        PackVersionPointer pointer = Assert.IsType<PackVersionPointer>(await second.Pack.GetVersionPointerAsync(1));
        await first.Pack.PutVersionPointerAsync(1, pointer.ServerManifestHash, pointer.ClientManifestHash);

        JsonElement body = await first.OkAsync("catalog-sweep", """{ "operator": "oid:8f2c" }""");

        Assert.False(body.GetProperty("ran").GetBoolean());
        Assert.Equal(ContentPackSweep.SkippedListingFailed, body.GetProperty("skipReason").GetString());
        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.NotEmpty(live);
        foreach (string hash in live)
        {
            Assert.True(await first.Pack.ExistsAsync(hash), hash);
        }

        Assert.DoesNotContain(
            await first.Store.ListAuditAsync(default, 0, 0, 100),
            entry => entry.Action == ContentAuditActions.Sweep);
    }
}
