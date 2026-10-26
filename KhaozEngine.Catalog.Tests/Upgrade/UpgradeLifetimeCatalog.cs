using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>The real older catalog and the two definitions used by the lifetime scenarios.</summary>
internal static class UpgradeLifetimeCatalog
{
    public static Task<ContentAuthoringStoreLease> CreateAsync(
        TemporaryCatalogDatabase files, ContentTypeRegistry registry, bool sqlite, IPackStore? packs = null)
    {
        packs ??= new ObservedFileSystemPacks(files.Pack());
        IContentAuthoringStore store = sqlite
            ? new SqliteContentAuthoringStore(files.ConnectionString, registry, packs)
            : new InMemoryContentAuthoringStore(registry, packs);
        return CreateAsync(store);
    }

    public static async Task<ContentAuthoringStoreLease> CreateAsync(IContentAuthoringStore store)
    {
        var lease = new ContentAuthoringStoreLease(store);
        try
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ApplyEditsAsync(
                [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
                UpgradeFixtures.Actor, UpgradeFixtures.Operator, "seed the older catalog");
            await store.PublishAsync(PublishFixtures.Request(0));
            return lease;
        }
        catch (Exception primary)
        {
            try
            {
                lease.Dispose();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Catalog setup and cleanup both failed", primary, cleanup);
            }

            throw;
        }
    }

    public static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        return new ContentUpgradeSet(
            UpgradeFixtures.Adds(UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row")),
            UpgradeFixtures.Adds(UpgradeHarness.SecondId, 2, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));
    }
}
