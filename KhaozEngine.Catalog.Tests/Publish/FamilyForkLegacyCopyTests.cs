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

/// <summary>
/// Issue 908 after the fix: a copy forked by 19.0.0 through 20.18.0 holds a plain id while naming the source
/// row's family. Lossless re-import refuses that shape with KEC0037, and removing the copy's family key from
/// the bundle is the documented remedy. A family fork whose next block would cross the ceiling is refused
/// where the old plain counter would have issued an id.
/// </summary>
public sealed class FamilyForkLegacyCopyTests
{
    const string FamilyKey = "swords";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentFamilyBlock LegacyBlock => new(1, 0, 16, 16, 17, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegacyCopyNamingItsSourceFamilyOutsideEveryBlockIsRefusedAndLeavesTheStoreEmpty(bool sqlite)
    {
        using var catalog = new Catalog(sqlite);
        await catalog.OpenAsync();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.ImportBundleAsync(LegacyBundle(FamilyKey), PublishFixtures.Actor, "oid:tests", "legacy"));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        ContentFinding finding = Assert.Single(refused.Findings);
        Assert.Equal("KEC0037", finding.Code);
        Assert.Equal(32, finding.Id);
        Assert.Equal(Thing, finding.Type);

        Assert.Equal(0, await catalog.Store.GetActiveVersionAsync());
        Assert.Empty(await catalog.Store.ListVersionsAsync());
        Assert.Empty(await catalog.Store.ListFamiliesAsync(default));
        ContentRowPage rows = await RowsAsync(catalog.Store);
        Assert.Equal(0, rows.Total);
        Assert.Empty(rows.Rows);
        Assert.Empty((await catalog.Store.ReadPublishBaselineAsync()).Rules);
        Assert.Null(await catalog.Store.GetOpenDraftAsync());
        Assert.Equal(new ContentIdHighWater(0, 0), await ((IContentIdPersistence)catalog.Store).ReadHighWaterAsync(Thing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegacyCopyWithItsFamilyKeyRemovedImportsWithIdsKeysRuleAndSourceFamilyUnchanged(bool sqlite)
    {
        using var catalog = new Catalog(sqlite);
        await catalog.OpenAsync();

        ContentPublishResult imported = await catalog.Store.ImportBundleAsync(
            LegacyBundle(null), PublishFixtures.Actor, "oid:tests", "legacy remedy");

        Assert.Equal(1, imported.VersionNumber);
        ContentRowPage rows = await RowsAsync(catalog.Store);
        Assert.Equal([16, 32], rows.Rows.Select(row => row.Id).ToArray());
        Assert.Equal(["source", "source_legacy"], rows.Rows.Select(row => row.Key.ToString()).ToArray());
        ContentRow source = rows.Rows[0];
        ContentRow copy = rows.Rows[1];
        Assert.Equal(25, source.Fields[0].Number);
        Assert.True(source.Fields[1].IsAbsent);
        Assert.Equal(10, copy.Fields[0].Number);
        Assert.Equal(1, copy.Fields[1].Number);
        Assert.False(source.IsRetired);
        Assert.False(copy.IsRetired);

        ContentFamily family = Assert.Single(await catalog.Store.ListFamiliesAsync(Thing));
        Assert.Equal(1, family.FamilyId);
        Assert.Equal(FamilyKey, family.FamilyKey);
        Assert.Equal([LegacyBlock], family.Blocks);
        Assert.Equal(1L, (await catalog.Store.GetRowHistoryAsync(Thing, 16))[0].FamilyId);
        Assert.Null((await catalog.Store.GetRowHistoryAsync(Thing, 32))[0].FamilyId);

        RemapRule rule = Assert.Single((await catalog.Store.ExportBundleAsync(1)).Rules);
        Assert.Equal(1, rule.Sequence);
        Assert.Equal(1, rule.IntroducedIn);
        Assert.Equal(Thing, rule.Type);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Equal(16, rule.FromId);
        Assert.Equal(32, rule.ToId);
        Assert.Empty(rule.Payload.ToArray());

        await ApplyAsync(catalog.Store, ContentEdit.Add(Thing, new ContentKey("plain"), PublishFixtures.Fields(30)));
        await catalog.Store.PublishAsync(PublishFixtures.Request(1));
        ContentRow plain = (await RowsAsync(catalog.Store)).Rows.Single(row => row.Key.ToString() == "plain");
        Assert.Equal(33, plain.Id);
        Assert.False(LegacyBlock.Contains(plain.Id));
        Assert.Null((await catalog.Store.GetRowHistoryAsync(Thing, 33))[0].FamilyId);
        Assert.Equal([LegacyBlock], Assert.Single(await catalog.Store.ListFamiliesAsync(Thing)).Blocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFullFamilyForkWhoseNextBlockCrossesCeilingFortyIsRefusedWhereAPlainIdWouldFit(bool sqlite)
    {
        using var catalog = new Catalog(sqlite, ceiling: 40);
        await catalog.OpenAsync();
        await SeedFullFamilyAsync(catalog);
        var persistence = (IContentIdPersistence)catalog.Store;
        ContentIdHighWater before = await persistence.ReadHighWaterAsync(Thing);
        ContentFamily full = Assert.Single(await catalog.Store.ListFamiliesAsync(Thing));
        Assert.Equal([new ContentFamilyBlock(1, 0, 16, 16, 32, 1)], full.Blocks);
        await ApplyAsync(catalog.Store, ContentEdit.Fork(Thing, 16, new ContentKey("source"),
            new ContentKey("source_legacy"), PublishFixtures.LegacyField, PublishFixtures.Fields(25)));

        ContentAuthoringException failure = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.PublishAsync(PublishFixtures.Request(1)));

        Assert.Equal(ContentAuthoringException.IdCeilingReason, failure.Reason);
        Assert.Equal(1, await catalog.Store.GetActiveVersionAsync());
        Assert.Single(await catalog.Store.ListVersionsAsync());
        ContentRowPage rows = await RowsAsync(catalog.Store);
        Assert.Equal(16, rows.Total);
        Assert.Equal(Enumerable.Range(16, 16).ToArray(), rows.Rows.Select(row => row.Id).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(rows.Rows, row => row.Id == 32 || row.Key.ToString() == "source_legacy");
        Assert.Equal(10, rows.Rows.Single(row => row.Key.ToString() == "source").Fields[0].Number);
        Assert.Empty((await catalog.Store.ExportBundleAsync(1)).Rules);
        Assert.Equal(before, await persistence.ReadHighWaterAsync(Thing));
        Assert.Equal(full.Blocks, Assert.Single(await catalog.Store.ListFamiliesAsync(Thing)).Blocks);
        ContentDraft pending = Assert.IsType<ContentDraft>(await catalog.Store.GetOpenDraftAsync());
        Assert.False(pending.IsFrozen);
        Assert.Single(pending.Changes.Edits);
    }

    /// <summary>
    /// The shape 20.18.0 exported: family block [16,32), source 16 in it, and the copy at 32, the plain id the
    /// counter issued once the block reservation had advanced issued_through to 31.
    /// </summary>
    static ContentBundle LegacyBundle(string? copyFamilyKey)
        => new(
            ContentBundle.CurrentFormatVersion,
            "legacy-store",
            2,
            [],
            [
                new ContentBundleRow(Thing, 16, new ContentKey("source"), false, FamilyKey, PublishFixtures.Fields(25)),
                new ContentBundleRow(Thing, 32, new ContentKey("source_legacy"), false, copyFamilyKey,
                [
                    new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 10)),
                    new ContentFieldEdit(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1)),
                ]),
            ],
            [new ContentFamily(1, Thing, FamilyKey, 16, false, 1, [LegacyBlock])],
            [new RemapRule(1, 2, Thing, RemapRuleKind.MovedToLegacy, 16, 32, [])]);

    static async Task SeedFullFamilyAsync(Catalog catalog)
    {
        ContentFamily family = await catalog.Store.CreateFamilyAsync(Thing, FamilyKey, 16, PublishFixtures.Actor, "oid:tests");
        ContentEdit[] edits = Enumerable.Range(0, 16).Select(index => ContentEdit.Add(Thing,
            new ContentKey(index == 0 ? "source" : "member_" + index.ToString(CultureInfo.InvariantCulture)),
            PublishFixtures.Fields(10 + index), family.FamilyId)).ToArray();
        await ApplyAsync(catalog.Store, edits);
        await catalog.Store.PublishAsync(PublishFixtures.Request(0));
    }

    static Task<ContentDraft> ApplyAsync(IContentAuthoringStore store, params ContentEdit[] edits)
        => store.ApplyEditsAsync(edits, PublishFixtures.Actor, "oid:tests", "legacy fork");

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
