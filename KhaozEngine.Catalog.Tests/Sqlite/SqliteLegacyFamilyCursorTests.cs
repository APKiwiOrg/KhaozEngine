using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

public sealed class SqliteLegacyFamilyCursorTests
{
    [Fact]
    public async Task AllocationRepairsABlockWhoseLastIdWasAlreadyPublished()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        ContentFamily family;
        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, registry, database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            family = await store.CreateFamilyAsync(type, "swords", ContentFamily.MinBlockSize, PublishFixtures.Actor, "oid:tests");
            ContentEdit imported = ContentEdit.Import(
                type, family.Blocks[0].TopExclusive - 1, new ContentKey("carried"), PublishFixtures.Fields(11), family.FamilyId);
            await store.ApplyEditsAsync([imported], PublishFixtures.Actor, "oid:tests", "publish a carried family id");
            await store.PublishAsync(PublishFixtures.Request(0));
        }

        // Issue #1552. Older publishers could commit this row without moving its persisted block cursor.
        database.Execute("UPDATE catalog_family_block SET next_free_id = base_id;");
        using var reopened = new SqliteContentAuthoringStore(database.ConnectionString, registry, database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        int allocated = await reopened.AllocateInFamilyAsync(family.FamilyId);

        Assert.True(allocated >= family.Blocks[0].TopExclusive);
        Assert.Equal(family.Blocks[0].TopExclusive - 1, Assert.Single(
            (await reopened.ListRowsAsync(type, 0, null, false, 0, 10)).Rows).Id);
        ContentFamily updated = Assert.Single(await reopened.ListFamiliesAsync(type));
        Assert.True(updated.Blocks[0].IsFull);
        Assert.True(updated.Blocks[1].Contains(allocated));
    }
}
