using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The SQL Server provider driven END TO END, the same shape the SQLite suite runs, so the behaviour
/// conformance suite is never the first thing to discover the provider is hollow: a draft, two publishes, the
/// temporal history they leave, the pack the second one is read back out of, and a bundle exported from a
/// database and imported into an empty one.
/// <para>
/// Every fact asserted here is a fact about the DATABASE rather than about the pipeline, which the in-memory
/// store already pins: the rows and their field values survive a publish and come back parallel to the
/// schema, the draft is gone afterwards, the active pointer moved, and a second publish against a stale base
/// is refused by the number the transaction re-reads.
/// </para>
/// <para>
/// The bundle round trip uses ONE database rather than two, because the fixture's isolation unit is the
/// schema of the one test instance rather than a file. It exports, drops the schema, and imports into what is
/// then a genuinely empty database with a freshly minted epoch, which is the same journey across two.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerContentAuthoringStoreTests
{
    const string Actor = "sqlserver-tests";
    const string Operator = "oid:tests";

    static ContentTypeId Thing => new(CatalogFixtures.ThingTypeId);

    static ContentTypeRegistry Registry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "sql server provider tests", expectedBaseVersion);

    static async Task<SqlServerContentAuthoringStore> OpenAsync(
        SqlServerCatalogDatabase database,
        ContentTypeRegistry registry,
        bool withPack = true)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, registry, withPack ? database.Pack() : null);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    [CatalogSqlServerFact]
    public async Task ADraftPublishesAndTheVersionReadsBackOutOfItsOwnPack()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        SqlServerContentAuthoringStore store = await OpenAsync(database, registry);

        ContentDraft draft = await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");

        Assert.Equal(0, draft.BaseVersion);
        Assert.Equal(2, draft.EditCount);

        ContentPublishResult published = await store.PublishAsync(Request(0));

        Assert.Equal(1, published.VersionNumber);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetOpenDraftAsync());

        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, false, 0, 10);
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.VersionNumber);
        Assert.Equal([1, 2], Ids(page.Rows));
        Assert.Equal(11, page.Rows[0].Fields[0].Number);

        // The snapshot comes out of the PACK rather than out of the row table, so this also proves the chunk
        // bytes the publish wrote are readable and hash to the names they were filed under.
        ContentSnapshot snapshot = await store.LoadSnapshotAsync(1, registry);
        Assert.Equal(1, snapshot.VersionNumber);
        Assert.True(snapshot.TryGetRow(Thing, 2, out ContentRow? row));
        Assert.Equal(22, row.Fields[0].Number);
        Assert.Equal("two", row.Key.ToString());
    }

    [CatalogSqlServerFact]
    public async Task ASecondPublishClosesTheOldRevisionAndLeavesBothInTheHistory()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        SqlServerContentAuthoringStore store = await OpenAsync(database, registry);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99))],
            Actor,
            Operator,
            "reprice");
        ContentPublishResult second = await store.PublishAsync(Request(1));

        Assert.Equal(2, second.VersionNumber);

        IReadOnlyList<ContentRowRevision> history = await store.GetRowHistoryAsync(Thing, 1);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history[0].ValidFromVersion);
        Assert.Equal(2, history[0].ReplacedInVersion);
        Assert.Equal(11, history[0].Row.Fields[0].Number);
        Assert.Equal(2, history[1].ValidFromVersion);
        Assert.Null(history[1].ReplacedInVersion);
        Assert.Equal(99, history[1].Row.Fields[0].Number);

        // What version 1 looked like is still a QUERY rather than an audit reconstruction.
        ContentRowPage atOne = await store.ListRowsAsync(Thing, 1, null, false, 0, 10);
        Assert.Equal(11, atOne.Rows[0].Fields[0].Number);

        ContentSnapshot snapshot = await store.LoadSnapshotAsync(2, registry);
        Assert.True(snapshot.TryGetRow(Thing, 1, out ContentRow? row));
        Assert.Equal(99, row.Fields[0].Number);

        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        Assert.Equal([2, 1], Numbers(versions));
    }

    [CatalogSqlServerFact]
    public async Task APublishAgainstAStaleBaseIsRefusedByTheNumberTheTransactionReReads()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry());

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22))],
            Actor,
            Operator,
            "add");

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Request(0)));
        Assert.Equal("base-version-moved", refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [CatalogSqlServerFact]
    public async Task TheAuditRecordsEveryFieldTheDraftAndThePublishTouched()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry());

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(Thing, 1, 0, 50);
        Assert.NotEmpty(audit);
        Assert.Contains(audit, entry => entry.Action == ContentAuditActions.Publish
            && entry.FieldName == CatalogFixtures.ValueField
            && entry.AfterValue == "11");
        Assert.All(audit, entry => Assert.Equal(Operator, entry.Operator));

        // The draft edit's own row carries no version, the publish rows carry the number they landed in.
        IReadOnlyList<ContentAuditEntry> everything = await store.ListAuditAsync(default, 0, 0, 50);
        Assert.Contains(everything, entry => entry.Action == ContentAuditActions.DraftEdit);
    }

    [CatalogSqlServerFact]
    public async Task AFamilyAllocatesFromItsOwnAlignedBlockAndThePlainCounterStaysOutOfIt()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry(), withPack: false);

        int plain = await store.AllocateAsync(Thing, 1);
        Assert.Equal(1, plain);

        ContentFamily family = await store.CreateFamilyAsync(Thing, "swords", 16, Actor, Operator);
        Assert.Single(family.Blocks);
        Assert.Equal(0, family.Blocks[0].BaseId % 16);
        Assert.Equal(1, family.CreatedInVersion);

        int member = await store.AllocateInFamilyAsync(family.FamilyId);
        Assert.Equal(family.Blocks[0].BaseId, member);
        Assert.True(family.Contains(member));

        // The block reservation advanced the type's issued mark past the block top, so the plain counter
        // cannot walk under the block and hand out an id inside it a second time.
        int next = await store.AllocateAsync(Thing, 1);
        Assert.False(family.Contains(next));
        Assert.True(next >= family.Blocks[0].TopExclusive);

        IReadOnlyList<ContentFamily> families = await store.ListFamiliesAsync(Thing);
        Assert.Single(families);
        Assert.Equal("swords", families[0].FamilyKey);
        Assert.Equal(member + 1, families[0].Blocks[0].NextFreeId);
    }

    [CatalogSqlServerFact]
    public async Task ABundleExportedFromADatabaseImportsIntoAnEmptyOneWithTheSameIds()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentTypeRegistry sourceRegistry = Registry();
        SqlServerContentAuthoringStore source = await OpenAsync(database, sourceRegistry);

        await source.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await source.PublishAsync(Request(0));

        await source.ApplyEditsAsync(
            [ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0)],
            Actor,
            Operator,
            "retire two");
        await source.PublishAsync(Request(1));

        ContentBundle bundle = await source.ExportBundleAsync(2);
        string sourceEpoch = await source.GetStoreEpochAsync();

        Assert.Equal(2, bundle.Rows.Count);
        Assert.Single(bundle.Rules);

        database.DropSchema();
        ContentTypeRegistry targetRegistry = Registry();
        SqlServerContentAuthoringStore imported = await OpenAsync(database, targetRegistry);

        ContentPublishResult republished = await imported.ImportBundleAsync(bundle, Actor, Operator, "seed");

        // An import REPUBLISHES at version 1: the ids and keys come across exactly, the version line does not.
        Assert.Equal(1, republished.VersionNumber);
        Assert.NotEqual(sourceEpoch, await imported.GetStoreEpochAsync());

        ContentRowPage page = await imported.ListRowsAsync(Thing, 0, null, true, 0, 10);
        Assert.Equal([1, 2], Ids(page.Rows));
        Assert.Equal(11, page.Rows[0].Fields[0].Number);
        Assert.False(page.Rows[0].IsRetired);

        // The retired row comes back RETIRED and no second retire rule is appended for it: the bundle already
        // carries the rule that retired it, which is what the imported_retired column is for.
        Assert.True(page.Rows[1].IsRetired);
        ContentSnapshot snapshot = await imported.LoadSnapshotAsync(1, targetRegistry);
        Assert.True(snapshot.IsRetired(Thing, 2));
        Assert.Single(snapshot.Rules);
        Assert.Equal(1, snapshot.Rules[0].IntroducedIn);

        // A second import is refused flat, which is the whole of the empty-database rule.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => imported.ImportBundleAsync(bundle, Actor, Operator, "seed again"));
        Assert.Equal("catalog-not-empty", refused.Reason);
    }

    [CatalogSqlServerFact]
    public async Task ARefusedImportTakesBackOnlyWhatItStagedAndTheStoreImportsAgain()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore source = await OpenAsync(database, Registry());
        await source.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await source.PublishAsync(Request(0));
        ContentBundle bundle = await source.ExportBundleAsync(1);

        // The same bundle with one row naming a field the type does not declare. It gets as far as writing its
        // families and its id marks and is then refused inside the edit call, which is a refusal BEFORE the
        // publish transaction ever opens.
        var broken = new ContentBundle(
            bundle.FormatVersion,
            bundle.StoreEpoch,
            bundle.SourceVersion,
            bundle.Types,
            [
                bundle.Rows[0],
                bundle.Rows[1] with
                {
                    Fields = [new ContentFieldEdit(
                        "not_declared", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1))],
                },
            ],
            bundle.Families,
            bundle.Rules);

        database.DropSchema();
        ContentTypeRegistry targetRegistry = Registry();
        SqlServerContentAuthoringStore imported = await OpenAsync(database, targetRegistry);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => imported.ImportBundleAsync(broken, Actor, Operator, "broken seed"));
        Assert.Equal("unknown-field", refused.Reason);

        // The five tables the commit owns were never written, so the reset has nothing to take back from them
        // and does not delete from any of them. The rule table in particular is append only, and a DELETE
        // against it here would be the one statement in this provider that mutates one.
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_version;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_chunk;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_remap_rule;"));
        Assert.Equal(0, await imported.GetActiveVersionAsync());
        Assert.Null(await imported.GetPinnedVersionAsync());

        // What the import DID stage is gone, which is what makes the store importable again.
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_id_high_water;"));
        Assert.Null(await imported.GetOpenDraftAsync());

        ContentPublishResult republished = await imported.ImportBundleAsync(bundle, Actor, Operator, "seed");
        Assert.Equal(1, republished.VersionNumber);
        ContentRowPage page = await imported.ListRowsAsync(Thing, 0, null, true, 0, 10);
        Assert.Equal([1, 2], Ids(page.Rows));
    }

    [CatalogSqlServerFact]
    public async Task ASecondEditOfOneTargetReplacesItAndADifferentOperationIsRefused()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry());

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        // Same target, same operation: the console saved the row twice and the newer edit wins the one slot.
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(50))],
            Actor,
            Operator,
            "first save");
        ContentDraft draft = await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(60))],
            Actor,
            Operator,
            "second save");

        Assert.Equal(1, draft.EditCount);
        Assert.Equal(60, draft.Changes.Edits[0].Fields[0].Value.Number);

        // Same target, DIFFERENT operation: refused rather than flipping the standing edit's operation.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ApplyEditsAsync(
                [ContentEdit.Retire(Thing, 1, new ContentKey("one"), ContentRetirePolicy.Placeholder, 0)],
                Actor,
                Operator,
                "retire"));
        Assert.Equal("edit-target-collision", refused.Reason);

        ContentDraft? standing = await store.GetOpenDraftAsync();
        Assert.NotNull(standing);
        Assert.Equal(1, standing.EditCount);
        Assert.Equal(ContentEditOperation.Update, standing.Changes.Edits[0].Operation);
    }

    [CatalogSqlServerFact]
    public async Task ThePinIsWrittenClearedAndRefusedForAVersionThatDoesNotExist()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry());

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.SetPinnedVersionAsync(1, Actor, Operator);
        Assert.Equal(1, await store.GetPinnedVersionAsync());

        await store.SetPinnedVersionAsync(null, Actor, Operator);
        Assert.Null(await store.GetPinnedVersionAsync());

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.SetPinnedVersionAsync(7, Actor, Operator));
    }

    [CatalogSqlServerFact]
    public async Task ARollbackBuildsADraftAndPublishesNothing()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry());

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(99))],
            Actor,
            Operator,
            "reprice");
        await store.PublishAsync(Request(1));

        ContentDraft draft = await store.RollbackToAsync(1, Actor, Operator, "undo the reprice");

        Assert.Equal(1, draft.EditCount);
        Assert.Equal(2, await store.GetActiveVersionAsync());

        ContentPublishResult third = await store.PublishAsync(Request(2));
        Assert.Equal(3, third.VersionNumber);

        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, false, 0, 10);
        Assert.Equal(11, page.Rows[0].Fields[0].Number);
    }

    [CatalogSqlServerFact]
    public async Task ADiscardedDraftLeavesNoEditsAndOneAuditRow()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, Registry(), withPack: false);

        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");

        await store.DiscardDraftAsync(Actor, Operator);

        Assert.Null(await store.GetOpenDraftAsync());
        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, 50);
        Assert.Contains(audit, entry => entry.Action == ContentAuditActions.DraftDiscard
            && entry.BeforeValue == "2");
    }

    static int[] Ids(IReadOnlyList<ContentRow> rows)
    {
        var ids = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            ids[i] = rows[i].Id;
        }

        return ids;
    }

    static int[] Numbers(IReadOnlyList<ContentVersionRecord> versions)
    {
        var numbers = new int[versions.Count];
        for (int i = 0; i < versions.Count; i++)
        {
            numbers[i] = versions[i].VersionNumber;
        }

        return numbers;
    }
}
