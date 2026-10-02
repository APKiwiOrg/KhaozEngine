using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

public sealed class FamilyForkPublicationTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AFreeFamilySlotSupportsPublicForkAndLosslessBundleIdentity(bool sourceSqlite, bool targetSqlite)
    {
        using var source = new Catalog(sourceSqlite);
        await source.OpenAsync();
        ContentFamily family = await SeedAsync(source, 1);
        await ForkAsync(source);

        ContentRowPage rows = await RowsAsync(source.Store);
        ContentRow original = rows.Rows.Single(row => row.Key.ToString() == "source");
        ContentRow legacy = rows.Rows.Single(row => row.Key.ToString() == "source_legacy");
        Assert.Equal(16, original.Id);
        Assert.Equal(17, legacy.Id);
        Assert.Equal(25, original.Fields[0].Number);
        Assert.True(original.Fields[1].IsAbsent);
        Assert.Equal(10, legacy.Fields[0].Number);
        Assert.Equal(1, legacy.Fields[1].Number);
        Assert.Equal(original.ParentId, legacy.ParentId);
        Assert.False(original.IsRetired);
        Assert.False(legacy.IsRetired);
        ContentFamily after = Assert.Single(await source.Store.ListFamiliesAsync(Thing));
        Assert.Single(after.Blocks);
        Assert.Contains(after.Blocks, block => block.Contains(legacy.Id));
        Assert.Equal(family.FamilyId, (await source.Store.GetRowHistoryAsync(Thing, legacy.Id))[0].FamilyId);

        ContentBundle exported = await source.Store.ExportBundleAsync(2);
        RemapRule rule = Assert.Single(exported.Rules);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Equal(16, rule.FromId);
        Assert.Equal(17, rule.ToId);
        Assert.Equal(2, rule.IntroducedIn);
        using var target = new Catalog(targetSqlite);
        await target.OpenAsync();
        await target.Store.ImportBundleAsync(exported, PublishFixtures.Actor, "oid:tests", "family fork");
        ContentBundle imported = await target.Store.ExportBundleAsync(1);

        Assert.Equal(exported.Rows.Count, imported.Rows.Count);
        foreach (ContentBundleRow expected in exported.Rows)
        {
            ContentBundleRow actual = imported.Rows.Single(row => row.Key.Equals(expected.Key));
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.FamilyKey, actual.FamilyKey);
            Assert.Equal(expected.IsRetired, actual.IsRetired);
            Assert.Equal(expected.Fields, actual.Fields);
        }
        ContentFamily restored = Assert.Single(imported.Families);
        Assert.Equal(after.FamilyId, restored.FamilyId);
        Assert.Equal(after.FamilyKey, restored.FamilyKey);
        Assert.Equal(after.Blocks, restored.Blocks);
        RemapRule carried = Assert.Single(imported.Rules);
        Assert.Equal(rule.Type, carried.Type);
        Assert.Equal(rule.Kind, carried.Kind);
        Assert.Equal(rule.Sequence, carried.Sequence);
        Assert.Equal(rule.FromId, carried.FromId);
        Assert.Equal(rule.ToId, carried.ToId);
        Assert.Equal(rule.Payload.ToArray(), carried.Payload.ToArray());
        Assert.Equal(1, carried.IntroducedIn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFullSixteenSlotFamilyReservesAnotherBlockAndPlainIdsStayOutsideAllBlocks(bool sqlite)
    {
        using var catalog = new Catalog(sqlite);
        await catalog.OpenAsync();
        ContentFamily family = await SeedAsync(catalog, 16);
        ContentFamily full = Assert.Single(await catalog.Store.ListFamiliesAsync(Thing));
        Assert.True(Assert.Single(full.Blocks).IsFull);

        await ForkAsync(catalog);

        ContentFamily after = Assert.Single(await catalog.Store.ListFamiliesAsync(Thing));
        Assert.Equal(2, after.Blocks.Count);
        ContentRow legacy = (await RowsAsync(catalog.Store)).Rows.Single(row => row.Key.ToString() == "source_legacy");
        Assert.Equal(32, legacy.Id);
        Assert.False(family.Blocks[0].Contains(legacy.Id));
        Assert.True(after.Blocks[1].Contains(legacy.Id));
        Assert.Equal(family.FamilyId, (await catalog.Store.GetRowHistoryAsync(Thing, legacy.Id))[0].FamilyId);
        await ApplyAsync(catalog.Store, ContentEdit.Add(Thing, new ContentKey("plain"), PublishFixtures.Fields(30)));
        await catalog.Store.PublishAsync(PublishFixtures.Request(2));
        ContentRow plain = (await RowsAsync(catalog.Store)).Rows.Single(row => row.Key.ToString() == "plain");
        Assert.Equal(48, plain.Id);
        Assert.DoesNotContain(after.Blocks, block => block.Contains(plain.Id));
        Assert.Null((await catalog.Store.GetRowHistoryAsync(Thing, plain.Id))[0].FamilyId);
        Assert.Single((await catalog.Store.ExportBundleAsync(3)).Rules);
    }

    [Fact]
    public async Task AFamilyBlockCeilingRefusalLeavesVisibleRowsRulesAndVersionUnchanged()
    {
        using var catalog = new Catalog(sqlite: false, ceiling: 31);
        await catalog.OpenAsync();
        await SeedAsync(catalog, 16);
        var persistence = (IContentIdPersistence)catalog.Store;
        ContentIdHighWater before = await persistence.ReadHighWaterAsync(Thing);
        ContentFamily full = Assert.Single(await catalog.Store.ListFamiliesAsync(Thing));
        await ApplyAsync(catalog.Store, Fork());

        ContentAuthoringException failure = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.PublishAsync(PublishFixtures.Request(1)));

        Assert.Equal(ContentAuthoringException.IdCeilingReason, failure.Reason);
        Assert.Equal(1, await catalog.Store.GetActiveVersionAsync());
        ContentRowPage rows = await RowsAsync(catalog.Store);
        Assert.Equal(16, rows.Total);
        Assert.DoesNotContain(rows.Rows, row => row.Key.ToString() == "source_legacy");
        Assert.Equal(10, rows.Rows.Single(row => row.Key.ToString() == "source").Fields[0].Number);
        Assert.Empty((await catalog.Store.ExportBundleAsync(1)).Rules);
        Assert.Equal(before, await persistence.ReadHighWaterAsync(Thing));
        Assert.Equal(full.Blocks, Assert.Single(await catalog.Store.ListFamiliesAsync(Thing)).Blocks);
        ContentDraft pending = Assert.IsType<ContentDraft>(await catalog.Store.GetOpenDraftAsync());
        Assert.False(pending.IsFrozen);
        Assert.Single(pending.Changes.Edits);
    }

    static async Task<ContentFamily> SeedAsync(Catalog catalog, int count)
    {
        ContentFamily family = await catalog.Store.CreateFamilyAsync(Thing, "swords", 16, PublishFixtures.Actor, "oid:tests");
        ContentEdit[] edits = Enumerable.Range(0, count).Select(index => ContentEdit.Add(Thing,
            new ContentKey(index == 0 ? "source" : "member_" + index.ToString(CultureInfo.InvariantCulture)),
            PublishFixtures.Fields(10 + index), family.FamilyId)).ToArray();
        await ApplyAsync(catalog.Store, edits);
        await catalog.Store.PublishAsync(PublishFixtures.Request(0));
        return family;
    }

    static ContentEdit Fork() => ContentEdit.Fork(Thing, 16, new ContentKey("source"), new ContentKey("source_legacy"),
        PublishFixtures.LegacyField, PublishFixtures.Fields(25));

    static async Task ForkAsync(Catalog catalog)
    {
        await ApplyAsync(catalog.Store, Fork());
        ContentPublishResult result = await catalog.Store.PublishAsync(PublishFixtures.Request(1));
        Assert.Equal(2, result.VersionNumber);
        Assert.Equal(1, result.RulesAppended);
    }

    static Task<ContentDraft> ApplyAsync(IContentAuthoringStore store, params ContentEdit[] edits)
        => store.ApplyEditsAsync(edits, PublishFixtures.Actor, "oid:tests", "family fork");

    static Task<ContentRowPage> RowsAsync(IContentAuthoringStore store)
        => store.ListRowsAsync(Thing, 0, null, true, 0, 100);

    sealed class Catalog : IDisposable
    {
        readonly TemporaryCatalogDatabase _database = new();

        public Catalog(bool sqlite, int? ceiling = null)
        {
            ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing with { MaxDefinitionId = ceiling });
            Store = sqlite
                ? new SqliteContentAuthoringStore(_database.ConnectionString, registry, _database.Pack())
                : new InMemoryContentAuthoringStore(registry, _database.Pack());
        }

        public IContentAuthoringStore Store { get; }

        public Task OpenAsync() => Store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        public void Dispose()
        {
            (Store as IDisposable)?.Dispose();
            _database.Dispose();
        }
    }
}
