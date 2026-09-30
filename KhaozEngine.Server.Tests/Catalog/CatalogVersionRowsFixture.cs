using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>A multi-type catalog with changes and retires beyond the first 500-row page.</summary>
internal sealed class CatalogVersionRowsFixture : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-version-rows-" + Guid.NewGuid().ToString("n"));

    public ContentTypeRegistry Registry { get; } = CatalogFixtures.Registry(
        CatalogFixtures.OtherSpec, CatalogFixtures.ThingSpec);

    public FileSystemPackStore Pack => new(_root);

    public async Task SeedAsync(IContentAuthoringStore store)
    {
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await CatalogVersionRowsReadAssertions.EmptyAsync(store);
        ContentEdit[] rows = Enumerable.Range(1, 501).Reverse().SelectMany(id => new[]
        {
            ContentEdit.Import(CatalogFixtures.Other, id, Key("other", id), CatalogFixtures.Fields(id)),
            ContentEdit.Import(CatalogFixtures.Thing, id, Key("thing", id), CatalogFixtures.Fields(id)),
        }).ToArray();
        await PublishAsync(store, rows);
        await PublishAsync(store,
            ContentEdit.Update(CatalogFixtures.Other, 500, Key("other", 500), CatalogFixtures.Fields(9002)),
            ContentEdit.Update(CatalogFixtures.Thing, 501, Key("thing", 501), CatalogFixtures.Fields(9001)),
            ContentEdit.Import(CatalogFixtures.Thing, 502, Key("thing", 502), CatalogFixtures.Fields(9003)));
    }

    public static async Task RetireAsync(IContentAuthoringStore store)
        => await PublishAsync(store,
            ContentEdit.Retire(CatalogFixtures.Other, 501, Key("other", 501), ContentRetirePolicy.Placeholder, replacementId: 0),
            ContentEdit.Retire(CatalogFixtures.Thing, 500, Key("thing", 500), ContentRetirePolicy.Placeholder, replacementId: 0));

    public static async Task PublishAsync(IContentAuthoringStore store, params ContentEdit[] edits)
    {
        int at = await store.GetActiveVersionAsync();
        await store.ApplyEditsAsync(edits, CatalogFixtures.Actor, CatalogFixtures.Operator, "version rows fixture");
        await store.PublishAsync(new ContentPublishRequest(
            CatalogFixtures.Actor, CatalogFixtures.Operator, "version rows fixture", at));
    }

    public static ContentKey Key(string type, int id)
        => new(FormattableString.Invariant($"{type}_{id}"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
