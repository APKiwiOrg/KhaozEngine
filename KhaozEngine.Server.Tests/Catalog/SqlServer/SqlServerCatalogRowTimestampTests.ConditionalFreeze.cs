using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The SQLite fact's twin: the guarded freeze and its recorded-base release move <c>catalog_draft.updated_at_utc</c>
/// only when they change the marker, and a refused freeze writes nothing, a stale marker included.
/// </summary>
public sealed partial class SqlServerCatalogRowTimestampTests
{
    [CatalogSqlServerFact]
    public async Task ConditionalFreeze_StampsOnlyMarkerChanges()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentBundle bundle = await SourceBundleAsync(database, withFamily: false);
        var clock = new ManualClock(T0);
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        IContentConditionalDraftFreeze safe = store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ImportBundleAsync(bundle, Actor, Operator, "seed");
        clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(12))], Actor, Operator, "v2");
        await store.PublishAsync(Request(1));

        // Active 2, a row draft, and a dead publish's marker naming base 0.
        clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(13))], Actor, Operator, "draft");
        clock.Tick();
        await store.FreezeDraftAsync(0);

        var beforeTimes = DraftTimes(database);
        clock.Tick();
        await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(beforeTimes, DraftTimes(database));
        Assert.Equal("0", Text(database, "SELECT CAST(frozen_for_base_version AS nvarchar(20)) FROM dbo.catalog_draft;"));

        DateTimeOffset replacedAt = clock.Tick();
        await safe.FreezeDraftForBaseAsync(2);
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        clock.Tick();
        await safe.FreezeDraftForBaseAsync(2);
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        clock.Tick();
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        Assert.Equal("2", Text(database, "SELECT CAST(frozen_for_base_version AS nvarchar(20)) FROM dbo.catalog_draft;"));

        DateTimeOffset releasedAt = clock.Tick();
        Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(2));
        Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
        clock.Tick();
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(2));
        Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
    }
}
