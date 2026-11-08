using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

public sealed class SqliteStoreRefusalTests : StoreRefusalTests
{
    protected override bool UsesSqlite => true;

    [Theory]
    [InlineData(0L)]
    [InlineData(4294967297L)]
    public async Task AFrozenDraftReportsTheCompleteStoredMarkerWithoutWriting(long marker)
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync([Add(value: 1)], Actor, Operator, "");
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        database.Execute(FormattableString.Invariant($"UPDATE catalog_draft SET frozen_for_base_version = {marker};"));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.ApplyEditsAsync([Add(value: 2)], Actor, Operator, ""));
        Assert.Equal(ContentAuthoringRefusal.PublishInProgress, refused.Refusal);
        Assert.Equal("publish-in-progress", refused.Reason);
        Assert.Equal("ApplyEditsAsync", refused.MemberName);
        Assert.Equal<long?>(marker, refused.FrozenForBaseVersion);
        Assert.Equal(1, database.Scalar("SELECT int_value FROM catalog_draft_edit_field WHERE field_name = 'value';"));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        await store.ClearDraftFreezeAsync();
        Assert.Equal(new[] { Value(2) }, Assert.Single((await store.ApplyEditsAsync([Add(value: 2)], Actor, Operator, "")).Changes.Edits).Fields);
    }

    [Fact]
    public async Task MissingMetadataRefusesReadsUntilTheCatalogRowIsRestored()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("CREATE TABLE saved_metadata AS SELECT * FROM catalog_metadata; DELETE FROM catalog_metadata;");
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetActiveVersionAsync());
        Assert.Equal(ContentAuthoringRefusal.SchemaMetadataMissing, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        database.Execute("INSERT INTO catalog_metadata SELECT * FROM saved_metadata; DROP TABLE saved_metadata;");
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task AHostTriggerThatRemovesTheOpenedDraftRefusesAndRollsBackItsOwnWrite()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("""
            CREATE TABLE host_draft_probe(value TEXT NOT NULL);
            CREATE TRIGGER host_remove_draft AFTER INSERT ON catalog_draft BEGIN
                INSERT INTO host_draft_probe VALUES ('written');
                DELETE FROM catalog_draft WHERE draft_key = NEW.draft_key;
            END;
            """);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.ApplyEditsAsync([], Actor, Operator, ""));
        Assert.Equal(ContentAuthoringRefusal.DraftRowDisappeared, refused.Refusal);
        Assert.Equal("no-open-draft", refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM host_draft_probe;"));
        database.Execute("DROP TRIGGER host_remove_draft;");
        Assert.Equal(0, (await store.ApplyEditsAsync([], Actor, Operator, "")).EditCount);
        Assert.NotNull(await store.GetOpenDraftAsync());
    }

    // RAISE(IGNORE) leaves the close affecting no row, so only the real commit path can see it. The probe row the
    // trigger writes first is what a lost rollback would leave behind.
    [Fact]
    public async Task AHostTriggerThatSuppressesATextCloseRefusesTheCommitAndRollsBackItsOwnWrite()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack(), () => DateTimeOffset.UnixEpoch);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await PublishNamedRowAsync(store, "sword", "Sword");
        await ApplyAsync(store, [ContentEdit.Update(Item, 1, Sword, [Value(2)])], ContentTextEdit.Set(Target("name", "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", ("sword", "Blade"));
        var (snapshot, rowPlan) = await FreezeAndPlanAsync(store, [new ManifestLanguageEntry("en", chunk.Hash)]);
        ContentTextRevision held = Assert.Single(snapshot.BaselineText.Revisions);
        var inserted = new ContentTextRevision(Item, 1, "name", "en", "Blade", 2, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate([inserted], [new("en", "en")], [held], [inserted]), [chunk]);
        database.Execute("""
            CREATE TABLE host_text_probe(value TEXT NOT NULL);
            CREATE TRIGGER host_keep_text BEFORE UPDATE ON catalog_text BEGIN
                INSERT INTO host_text_probe VALUES ('written');
                SELECT RAISE(IGNORE);
            END;
            """);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.CommitTextPublishAsync(plan, Request(1), null));

        Assert.Equal(ContentAuthoringRefusal.TextCloseRevisionMissing, refused.Refusal);
        Assert.Equal(Item, refused.TextRowType);
        Assert.Equal(1, refused.TextRowId);
        Assert.Equal(1, refused.FromVersion);
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM host_text_probe;"));
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
        Assert.Equal("Sword", Assert.Single((await store.ReadTextSnapshotAsync(1)).Revisions).Value);

        database.Execute("DROP TRIGGER host_keep_text;");
        Assert.Equal(2, (await store.CommitTextPublishAsync(plan, Request(1), null)).VersionNumber);
        Assert.Equal("Blade", Assert.Single((await store.ReadTextSnapshotAsync(2)).Revisions).Value);
    }

    [Fact]
    public async Task AnUnrecognizedStoredOperationNamesItsPersistedOrdinalAndReadsAfterRepair()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        ContentDraft before = await store.ApplyEditsAsync(
            [ContentEdit.Import(Item, 7, Sword, [Value(1)])], Actor, Operator, "");
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        database.Execute("PRAGMA ignore_check_constraints = ON; UPDATE catalog_draft_edit SET operation = 99;");
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetOpenDraftAsync());
        Assert.Equal(ContentAuthoringRefusal.StoredEditOperationUnknown, refused.Refusal);
        Assert.Equal("unknown-edit-operation", refused.Reason);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(7, refused.Id);
        Assert.Equal(99, refused.ActualOperation);
        Assert.Equal(1L, refused.StoredEditOrdinal);
        Assert.Equal(99, database.Scalar("SELECT operation FROM catalog_draft_edit;"));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        database.Execute("UPDATE catalog_draft_edit SET operation = 1;");
        Assert.True(ContentTextCompatibility.SameDraft(before, (await store.GetOpenDraftAsync())!));
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("epoch")]
    [InlineData("text")]
    [InlineData("bundle")]
    public async Task MalformedStoredScalarsReportTheirTypesRatherThanMissingMetadata(string route)
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        if (route is "text" or "bundle")
        {
            await store.ApplyEditsAsync([Add()], Actor, Operator, "");
            await store.PublishAsync(Request(0));
        }
        string epoch = await store.GetStoreEpochAsync();
        bool integer = route == "integer";
        database.Execute(integer ? "UPDATE catalog_metadata SET active_version = 'bad';"
            : "UPDATE catalog_metadata SET store_epoch = CAST(store_epoch AS BLOB);");
        Task Read() => route switch
        {
            "integer" => store.GetActiveVersionAsync(),
            "epoch" => store.GetStoreEpochAsync(),
            "text" => store.ReadTextSnapshotAsync(1),
            "bundle" => store.ExportBundleAsync(1),
            _ => throw new InvalidOperationException(),
        };
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(Read);
        Assert.Equal(ContentAuthoringRefusal.StoredScalarTypeMismatch, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Equal(integer ? typeof(long) : typeof(string), refused.ExpectedScalarType);
        Assert.Equal(integer ? typeof(string) : typeof(byte[]), refused.ActualScalarType);
        database.Execute(integer ? "UPDATE catalog_metadata SET active_version = 0;"
            : "UPDATE catalog_metadata SET store_epoch = CAST(store_epoch AS TEXT);");
        await Read();
        Assert.Equal(epoch, await store.GetStoreEpochAsync());
        Assert.Equal(route is "text" or "bundle" ? 1 : 0, await store.GetActiveVersionAsync());
    }
}
