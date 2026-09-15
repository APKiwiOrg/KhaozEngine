using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The SQLite provider driven END TO END, so the behaviour conformance suite is never the first thing to
/// discover the provider is hollow: a draft, two publishes, the temporal history they leave, the pack the
/// second one is read back out of, and a bundle exported from one database and imported into an empty one.
/// <para>
/// Every fact asserted here is a fact about the DATABASE rather than about the pipeline, which the in-memory
/// store already pins: the rows and their field values survive a publish and come back parallel to the
/// schema, the draft is gone afterwards, the active pointer moved, and a second publish against a stale base
/// is refused by the number the transaction re-reads.
/// </para>
/// </summary>
public class SqliteContentAuthoringStoreTests
{
    const string Actor = "sqlite-tests";
    const string Operator = "oid:tests";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentTypeRegistry Registry() => PublishFixtures.Registry(PublishFixtures.Thing);

    static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "sqlite provider tests", expectedBaseVersion);

    [Fact]
    public async Task ADraftPublishesAndTheVersionReadsBackOutOfItsOwnPack()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        ContentDraft draft = await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
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

    [Fact]
    public async Task ASecondPublishClosesTheOldRevisionAndLeavesBothInTheHistory()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(99))],
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

    [Fact]
    public async Task APublishAgainstAStaleBaseIsRefusedByTheNumberTheTransactionReReads()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22))],
            Actor,
            Operator,
            "add");

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Request(0)));
        Assert.Equal("base-version-moved", refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task TheAuditRecordsEveryFieldTheDraftAndThePublishTouched()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(Thing, 1, 0, 50);
        Assert.NotEmpty(audit);
        Assert.Contains(audit, entry => entry.Action == ContentAuditActions.Publish
            && entry.FieldName == PublishFixtures.ValueField
            && entry.AfterValue == "11");
        Assert.All(audit, entry => Assert.Equal(Operator, entry.Operator));

        // The draft edit's own row carries no version, the publish rows carry the number they landed in.
        IReadOnlyList<ContentAuditEntry> everything = await store.ListAuditAsync(default, 0, 0, 50);
        Assert.Contains(everything, entry => entry.Action == ContentAuditActions.DraftEdit);
    }

    [Fact]
    public async Task AFamilyAllocatesFromItsOwnAlignedBlockAndThePlainCounterStaysOutOfIt()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

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

    [Fact]
    public async Task ABundleExportedFromOneDatabaseImportsIntoAnEmptyOneWithTheSameIds()
    {
        using var source = new TemporaryCatalogDatabase();
        ContentTypeRegistry sourceRegistry = Registry();
        ContentBundle bundle;
        string sourceEpoch;
        using (var store = new SqliteContentAuthoringStore(
            source.ConnectionString, sourceRegistry, source.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ApplyEditsAsync(
                [
                    ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
                    ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
                ],
                Actor,
                Operator,
                "two adds");
            await store.PublishAsync(Request(0));

            await store.ApplyEditsAsync(
                [ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0)],
                Actor,
                Operator,
                "retire two");
            await store.PublishAsync(Request(1));

            bundle = await store.ExportBundleAsync(2);
            sourceEpoch = await store.GetStoreEpochAsync();
        }

        Assert.Equal(2, bundle.Rows.Count);
        Assert.Single(bundle.Rules);

        using var target = new TemporaryCatalogDatabase();
        ContentTypeRegistry targetRegistry = Registry();
        using var imported = new SqliteContentAuthoringStore(
            target.ConnectionString, targetRegistry, target.Pack());
        await imported.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        ContentPublishResult republished = await imported.ImportBundleAsync(bundle, Actor, Operator, "seed");

        // An import REPUBLISHES at version 1: the ids and keys come across exactly, the version line does not.
        Assert.Equal(1, republished.VersionNumber);
        Assert.NotEqual(sourceEpoch, await imported.GetStoreEpochAsync());

        ContentRowPage page = await imported.ListRowsAsync(Thing, 0, null, true, 0, 10);
        Assert.Equal([1, 2], Ids(page.Rows));
        Assert.Equal(11, page.Rows[0].Fields[0].Number);
        Assert.False(page.Rows[0].IsRetired);

        // The retired row comes back RETIRED and no second retire rule is appended for it: the bundle
        // already carries the rule that retired it.
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

    [Fact]
    public async Task ASecondEditOfOneTargetReplacesItAndADifferentOperationIsRefused()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        // Same target, same operation: the console saved the row twice and the newer edit wins the one slot.
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(50))],
            Actor,
            Operator,
            "first save");
        ContentDraft draft = await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(60))],
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

    [Fact]
    public async Task ThePinIsWrittenClearedAndRefusedForAVersionThatDoesNotExist()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
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

    [Fact]
    public async Task ARollbackBuildsADraftAndPublishesNothing()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11))],
            Actor,
            Operator,
            "add");
        await store.PublishAsync(Request(0));

        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(99))],
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

    [Fact]
    public async Task ADiscardedDraftLeavesNoEditsAndOneAuditRow()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
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
