using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The guarded freeze and its recorded-base release move <c>catalog_draft.updated_at_utc</c> only when they change
/// the marker. A refused freeze writes nothing, a stale marker included, because the guarded freeze runs no sweep.
/// </summary>
public sealed partial class SqliteCatalogRowTimestampTests
{
    [Fact]
    public async Task ConditionalFreeze_StampsOnlyMarkerChanges()
    {
        ContentBundle bundle = await SourceBundleAsync(withFamily: false);
        using var database = new TemporaryCatalogDatabase();
        var clock = new ManualClock(T0);
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        IContentConditionalDraftFreeze safe = store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ImportBundleAsync(bundle, Actor, Operator, "seed");
        clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(12))], Actor, Operator, "v2");
        await store.PublishAsync(Request(1));

        // Active 2, a row draft, and a dead publish's marker naming base 0.
        clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(13))], Actor, Operator, "draft");
        clock.Tick();
        await store.FreezeDraftAsync(0);

        var beforeTimes = DraftTimes(database);
        clock.Tick();
        await Assert.ThrowsAsync<ContentAuthoringException>(() => safe.FreezeDraftForBaseAsync(1));
        Assert.Equal(beforeTimes, DraftTimes(database));
        Assert.Equal(0L, Value(database, "SELECT frozen_for_base_version FROM catalog_draft;"));

        long replacedAt = clock.Tick();
        await safe.FreezeDraftForBaseAsync(2);
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        clock.Tick();
        await safe.FreezeDraftForBaseAsync(2);
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        clock.Tick();
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(1));
        Assert.Equal((beforeTimes.Item1, replacedAt), DraftTimes(database));
        Assert.Equal(2L, Value(database, "SELECT frozen_for_base_version FROM catalog_draft;"));

        long releasedAt = clock.Tick();
        Assert.True(await safe.ReleaseDraftFreezeForBaseAsync(2));
        Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
        clock.Tick();
        Assert.False(await safe.ReleaseDraftFreezeForBaseAsync(2));
        Assert.Equal((beforeTimes.Item1, releasedAt), DraftTimes(database));
        Assert.Null(Value(database, "SELECT frozen_for_base_version FROM catalog_draft;"));
    }
}
