using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>Ordinary atomic publication of carried and allocator-owned identities in the same type.</summary>
public sealed class ContentMixedIdPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AFreshCounterPreservesTheImportedIdAndAllocatesAboveIt(bool sqlite, bool importFirst)
    {
        using var catalog = new PublicationCatalog(sqlite);
        IContentAuthoringStore store = catalog.Store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        Assert.Equal(new ContentIdHighWater(0, 0), await ((IContentIdPersistence)store).ReadHighWaterAsync(type));

        ContentEdit imported = ContentEdit.Import(type, 1, new ContentKey("named"), PublishFixtures.Fields(11));
        ContentEdit added = ContentEdit.Add(type, new ContentKey("allocated"), PublishFixtures.Fields(22));
        await store.ApplyEditsAsync(
            importFirst ? [imported, added] : [added, imported],
            PublishFixtures.Actor,
            "oid:tests",
            "mixed identity publication");

        ContentPublishResult published = await PublishAsync(store, 0);

        Assert.Equal(1, published.VersionNumber);
        ContentRowPage rows = await store.ListRowsAsync(type, 0, null, true, 0, 10);
        Assert.Equal(2, rows.Total);
        Assert.Collection(
            rows.Rows,
            row =>
            {
                Assert.Equal(1, row.Id);
                Assert.Equal("named", row.Key.ToString());
                Assert.Equal(11, row.Fields[0].Number);
            },
            row =>
            {
                Assert.Equal(2, row.Id);
                Assert.Equal("allocated", row.Key.ToString());
                Assert.Equal(22, row.Fields[0].Number);
            });
        Assert.Single(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExistingReservationObservesCarriedMaximaBeforeIssuingInEditOrder(bool sqlite)
    {
        using var catalog = new PublicationCatalog(sqlite);
        IContentAuthoringStore store = catalog.Store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);

        Assert.Equal(1, await store.AllocateAsync(type, 1));
        Assert.Equal(
            new ContentIdHighWater(ContentIdAllocator.ReserveBatch, 1),
            await ((IContentIdPersistence)store).ReadHighWaterAsync(type));
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(type, new ContentKey("first"), PublishFixtures.Fields(11)),
                ContentEdit.Import(type, 100, new ContentKey("named"), PublishFixtures.Fields(22)),
                ContentEdit.Add(type, new ContentKey("second"), PublishFixtures.Fields(33)),
            ],
            PublishFixtures.Actor, "oid:tests", "reuse the reservation");

        Assert.Equal(1, (await PublishAsync(store, 0)).VersionNumber);

        ContentRowPage rows = await store.ListRowsAsync(type, 0, null, true, 0, 10);
        Assert.Equal(3, rows.Total);
        Assert.Equal([100, 101, 102], rows.Rows.Select(row => row.Id));
        Assert.Equal(["named", "first", "second"], rows.Rows.Select(row => row.Key.ToString()));
        // This is the same store-owned allocator used before publication, with its existing reservation.
        Assert.Equal(103, await store.AllocateAsync(type, 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ACarriedIdAtTheCeilingRefusesAnAdditionWithoutPublishingRows(bool sqlite, bool importFirst)
    {
        using var catalog = new PublicationCatalog(sqlite, PublishFixtures.CappedThing);
        IContentAuthoringStore store = catalog.Store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        ContentEdit imported = ContentEdit.Import(
            type, PublishFixtures.Ceiling, new ContentKey("named"), PublishFixtures.Fields(11));
        ContentEdit added = ContentEdit.Add(type, new ContentKey("allocated"), PublishFixtures.Fields(22));
        await store.ApplyEditsAsync(
            importFirst ? [imported, added] : [added, imported],
            PublishFixtures.Actor, "oid:tests", "carried ceiling");

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(PublishFixtures.Request(0)));

        Assert.Equal(ContentAuthoringException.IdCeilingReason, refused.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal(0, (await store.ListRowsAsync(type, 0, null, true, 0, 10)).Total);
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.EditCount);
        Assert.Equal(
            new ContentIdHighWater(PublishFixtures.Ceiling, PublishFixtures.Ceiling),
            await ((IContentIdPersistence)store).ReadHighWaterAsync(type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewFamilyBlockAndPlainIdsStayAboveCarriedMaxima(bool sqlite)
    {
        using var catalog = new PublicationCatalog(sqlite);
        IContentAuthoringStore store = catalog.Store;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        ContentFamily family = await store.CreateFamilyAsync(type, "members", 16, PublishFixtures.Actor, "oid:tests");
        await store.ApplyEditsAsync(
            Enumerable.Range(0, 16).Select(index => ContentEdit.Add(
                type, new ContentKey(FormattableString.Invariant($"member_{index}")),
                PublishFixtures.Fields(index), family.FamilyId)).ToArray(),
            PublishFixtures.Actor, "oid:tests", "fill the first family block");
        await PublishAsync(store, 0);
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(type, new ContentKey("family_member"), PublishFixtures.Fields(11), family.FamilyId),
                ContentEdit.Import(type, 100, new ContentKey("named"), PublishFixtures.Fields(22)),
                ContentEdit.Add(type, new ContentKey("plain"), PublishFixtures.Fields(33)),
            ],
            PublishFixtures.Actor, "oid:tests", "open a family block above the imported id");

        Assert.Equal(2, (await PublishAsync(store, 1)).VersionNumber);

        ContentRowPage rows = await store.ListRowsAsync(type, 0, null, true, 0, 100);
        Assert.Equal(19, rows.Total);
        Assert.Equal(100, rows.Rows.Single(row => row.Key.ToString() == "named").Id);
        Assert.Equal(112, rows.Rows.Single(row => row.Key.ToString() == "family_member").Id);
        Assert.Equal(128, rows.Rows.Single(row => row.Key.ToString() == "plain").Id);
        ContentFamily updated = Assert.Single(await store.ListFamiliesAsync(type));
        Assert.Equal([16, 112], updated.Blocks.Select(block => block.BaseId));
        Assert.Equal(129, await store.AllocateAsync(type, 1));
    }

    static async Task<ContentPublishResult> PublishAsync(IContentAuthoringStore store, int expectedVersion)
    {
        try
        {
            return await store.PublishAsync(PublishFixtures.Request(expectedVersion));
        }
        catch (ContentAuthoringException refused)
        {
            Assert.Fail(refused.Message + " | " + string.Join(" | ", refused.Findings.Select(
                finding => finding.Code + ": " + finding.Message)));
            throw;
        }
    }

    sealed class PublicationCatalog : IDisposable
    {
        readonly TemporaryCatalogDatabase _files = new();
        readonly SqliteContentAuthoringStore? _database;

        public PublicationCatalog(bool sqlite, PublishTypeSpec? type = null)
        {
            ContentTypeRegistry registry = PublishFixtures.Registry(type ?? PublishFixtures.Thing);
            FileSystemPackStore packs = _files.Pack();
            _database = sqlite ? new SqliteContentAuthoringStore(_files.ConnectionString, registry, packs) : null;
            Store = _database is null ? new InMemoryContentAuthoringStore(registry, packs) : _database;
        }

        public IContentAuthoringStore Store { get; }

        public void Dispose()
        {
            _database?.Dispose();
            _files.Dispose();
        }
    }
}
