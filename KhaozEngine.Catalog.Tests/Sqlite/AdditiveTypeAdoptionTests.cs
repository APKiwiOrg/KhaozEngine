using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>Adding a registered content type to a populated catalog without replacing its history.</summary>
public sealed class AdditiveTypeAdoptionTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentTypeId Other => new(PublishFixtures.OtherTypeId);

    [Fact]
    public async Task ASupersetRegistryAddsATypeAndPublishesOverTheExistingCatalog()
    {
        using var database = new TemporaryCatalogDatabase();

        ContentTypeRegistry oldRegistry = PublishFixtures.Registry(PublishFixtures.Thing);
        using (var original = new SqliteContentAuthoringStore(
            database.ConnectionString, oldRegistry, database.Pack()))
        {
            await original.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await original.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
                PublishFixtures.Actor,
                "oid:adoption",
                "publish old registry");
            ContentPublishResult first = await original.PublishAsync(PublishFixtures.Request(0));
            Assert.Equal(1, first.VersionNumber);
        }

        ContentTypeRegistry superset = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        var packs = database.Pack();
        using var adopted = new SqliteContentAuthoringStore(database.ConnectionString, superset, packs);
        await adopted.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        await adopted.ApplyEditsAsync(
            [ContentEdit.Add(Other, new ContentKey("new_row"), PublishFixtures.Fields(22))],
            PublishFixtures.Actor,
            "oid:adoption",
            "adopt additive type");
        ContentPublishResult second = await adopted.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(2, await adopted.GetActiveVersionAsync());
        Assert.Equal([2, 1], (await adopted.ListVersionsAsync()).Select(version => version.VersionNumber));

        ContentSnapshot atOne = await adopted.LoadSnapshotAsync(1, oldRegistry);
        Assert.True(atOne.TryGetRow(Thing, 1, out ContentRow? oldAtOne));
        Assert.Equal(11, oldAtOne.Fields[0].Number);

        ContentSnapshot atTwo = await adopted.LoadSnapshotAsync(2, superset);
        Assert.True(atTwo.TryGetRow(Thing, 1, out ContentRow? oldAtTwo));
        Assert.Equal(11, oldAtTwo.Fields[0].Number);
        Assert.True(atTwo.TryGetRow(Other, 1, out ContentRow? added));
        Assert.Equal("new_row", added.Key.ToString());
        Assert.Equal(22, added.Fields[0].Number);

        ContentRowRevision oldHistory = Assert.Single(await adopted.GetRowHistoryAsync(Thing, 1));
        Assert.Equal(1, oldHistory.ValidFromVersion);
        Assert.Null(oldHistory.ReplacedInVersion);
        ContentRowRevision newHistory = Assert.Single(await adopted.GetRowHistoryAsync(Other, 1));
        Assert.Equal(2, newHistory.ValidFromVersion);
        Assert.Null(newHistory.ReplacedInVersion);

        superset.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = superset,
            Store = packs,
            Pointers = packs,
            Holder = holder,
            ConfiguredVersion = 2,
            ServerBuild = int.MaxValue,
            StoreName = "the additive adoption test store",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.Equal(2, boot.Runtime!.VersionNumber);
        Assert.True(boot.Runtime.TryGetRow(Thing, 1, out _));
        Assert.True(boot.Runtime.TryGetRow(Other, 1, out ContentRow? booted));
        Assert.Equal(22, booted.Fields[0].Number);
        Assert.Same(boot.Runtime, holder.Current);
    }
}
