using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The catalog RESET against a real database file: what it leaves behind, what it refuses, and the one
/// property the whole feature exists for, which is that a bundle imported after a reset publishes as version
/// 1 with the bundle's own ids and a manifest byte-identical to the one a never-used store would produce.
/// <para>
/// Two facts here are load bearing and each has a test whose only job is to hold it. The reset is ALL OR
/// NOTHING, because a half-dropped catalog refuses the next open outright: the initializer creates only when
/// it counts zero catalog tables and validates every object by name otherwise. And the reset drops EVERY
/// table, because a table left standing keeps both its rows and its <c>sqlite_sequence</c> mark, and the mark
/// is what makes a later family id land above the bundle's.
/// </para>
/// </summary>
public class SqliteCatalogResetTests
{
    const string Actor = SqliteCatalogResetHarness.Actor;
    const string Operator = SqliteCatalogResetHarness.Operator;

    static ContentTypeId Thing => SqliteCatalogResetHarness.Thing;

    static ContentTypeRegistry Registry() => SqliteCatalogResetHarness.Registry();

    static ContentPublishRequest Request(int expectedBaseVersion)
        => SqliteCatalogResetHarness.Request(expectedBaseVersion);

    [Fact]
    public async Task AResetLeavesASchemaTheInitializerValidatesAndACatalogThatPublishedNothing()
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await Seed(store, "one", "two");
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("three"), PublishFixtures.Fields(33))],
                Actor,
                Operator,
                "one more");
            await store.PublishAsync(Request(1));
            epochBefore = await store.GetStoreEpochAsync();
        }

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        // What stood is the only thing a caller can still learn after the drop, so the record carries it.
        Assert.Equal(2, reset.ActiveVersion);
        Assert.Equal(64, reset.ServerManifestHash!.Length);
        Assert.Equal(64, reset.ClientManifestHash!.Length);
        Assert.Equal(2, reset.VersionsDropped);
        Assert.Equal(3, reset.RowsDropped);
        Assert.Contains("version 2", reset.Summary, StringComparison.Ordinal);

        // A reset store IS a new store: the recreate mints an epoch, so a durable page stamped against the
        // old one cannot be taken for a page of this catalog.
        Assert.Equal(32, reset.StoreEpoch.Length);
        Assert.NotEqual(epochBefore, reset.StoreEpoch);

        // ValidateOnly is the production mode and it compares every object against the DDL, so this is the
        // recreate being checked by the same code a server boot would check it with.
        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(SqliteCatalogSchema.CurrentVersion, await reopened.GetSchemaVersionAsync());
        Assert.Equal(SqliteCatalogSchema.CurrentVersion, reset.SchemaVersion);
        Assert.Equal(SqliteCatalogSchema.CurrentVersion, reset.PriorSchemaVersion);
        Assert.Equal(0, await reopened.GetActiveVersionAsync());
        Assert.Null(await reopened.GetPinnedVersionAsync());
        Assert.Equal(reset.StoreEpoch, await reopened.GetStoreEpochAsync());

        // The table count is read off a store that has only ever been created rather than written here as a
        // number, so a schema that gains a table does not quietly move what this compares against. The count
        // does NOT prove the drop skipped nothing: a table left standing is still counted, and the recreate
        // would find it and leave it. What catches a skipped table that held rows is
        // AResetDropsEveryTableSoNoRowAndNoIdentityMarkSurvivesIt, which counts the ROWS of every table the
        // inventory names.
        using var fresh = new TemporaryCatalogDatabase();
        using (var creating = new SqliteContentAuthoringStore(fresh.ConnectionString, Registry()))
        {
            await creating.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        Assert.Equal(CountTables(fresh), CountTables(database));
    }

    [Fact]
    public async Task AResetReportsTheActiveVersionAndNotAPinBelowIt()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentVersionRecord active;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await Seed(store, "one", "two");
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("three"), PublishFixtures.Fields(33))],
                Actor,
                Operator,
                "one more");
            await store.PublishAsync(Request(1));
            await store.SetPinnedVersionAsync(1, Actor, Operator);
            active = (await store.GetVersionAsync(2))!;
        }

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        // A boot would have served the pin, version 1. The result names the active version and says so.
        Assert.Equal(2, reset.ActiveVersion);
        Assert.Equal(active.ServerManifestHash, reset.ServerManifestHash);
        Assert.Equal(active.ClientManifestHash, reset.ClientManifestHash);
        Assert.Contains(
            "active version 2, server manifest " + active.ServerManifestHash, reset.Summary, StringComparison.Ordinal);

        // The result is not the only place the hashes outlive the drop: the reset's own audit row files them.
        string filed = Text(database, "SELECT before_value FROM catalog_audit WHERE action = 'reset';");
        Assert.Contains(active.ServerManifestHash, filed, StringComparison.Ordinal);
        Assert.Contains(active.ClientManifestHash, filed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResetDropsEveryTableSoNoRowAndNoIdentityMarkSurvivesIt()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.CreateFamilyAsync(Thing, "swords", 16, Actor, Operator);
            await Seed(store, "one", "two");
        }

        await SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release");

        // Every catalog table, read back by NAME from the recreated database rather than listed here, so a
        // table the drop skipped is caught by the same loop whenever it held rows before the reset. A table
        // that was already empty reads the same either way, and this script creates only what is missing, so
        // nothing here would catch a skip of one of those.
        foreach (string table in Tables(database))
        {
            long expected = table switch
            {
                "catalog_metadata" => 1L,
                "catalog_audit" => 1L,
                _ => 0L,
            };
            Assert.Equal(
                expected,
                database.Scalar("SELECT COUNT(*) FROM \"" + table + "\";"));
        }

        // The identity state restarted with the tables. A DELETE-based reset would leave the audit mark high
        // and hand this row an id in the hundreds, and would do the same to the next family id.
        Assert.Equal(1L, database.Scalar("SELECT audit_id FROM catalog_audit;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'catalog_family';"));

        // catalog_audit went with everything else, so the reset records ITSELF in the new store. The schema
        // takes the row as it stands: no version is attached and nothing about the table had to bend.
        Assert.Equal("reset", Text(database, "SELECT action FROM catalog_audit;"));
        Assert.Equal(Actor, Text(database, "SELECT actor FROM catalog_audit;"));
        Assert.Equal("content release", Text(database, "SELECT note FROM catalog_audit;"));
        Assert.Equal(0L, database.Scalar("SELECT version_number FROM catalog_audit;"));
        Assert.Contains(
            "version 1",
            Text(database, "SELECT before_value FROM catalog_audit;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAResetABundleImportsAsVersionOneWithItsOwnIdsAndAFreshStoresManifest()
    {
        ContentBundle first = await Bundle("alpha_family", "one", "two");
        ContentBundle second = await Bundle("beta_family", "left", "middle", "right");

        // The store is taken PAST version 1 and past the bundle's family ids, which is the state a content
        // release finds and the state ImportBundleAsync refuses outright.
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ImportBundleAsync(first, Actor, Operator, "first seed");
            await store.CreateFamilyAsync(Thing, "later_one", 16, Actor, Operator);
            await store.CreateFamilyAsync(Thing, "later_two", 16, Actor, Operator);
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("extra"), PublishFixtures.Fields(99))],
                Actor,
                Operator,
                "extra");
            await store.PublishAsync(Request(1));
            Assert.Equal(2, await store.GetActiveVersionAsync());

            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => store.ImportBundleAsync(second, Actor, Operator, "second seed"));
            Assert.Equal("catalog-not-empty", refused.Reason);
        }

        await SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release");

        ContentTypeRegistry registry = Registry();
        using var replaced = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack());
        await replaced.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        ContentPublishResult imported = await replaced.ImportBundleAsync(
            second, Actor, Operator, "second seed");

        Assert.Equal(1, imported.VersionNumber);
        ContentRowPage page = await replaced.ListRowsAsync(Thing, 0, null, true, 0, 10);
        Assert.Equal(BundleIds(second), Ids(page.Rows));
        Assert.Equal("left", page.Rows[0].Key.ToString());

        // The bundle's family ids come back exactly, and the NEXT family carries on from them rather than
        // from the mark the replaced catalog left, which is what a plain delete could not give.
        IReadOnlyList<ContentFamily> families = await replaced.ListFamiliesAsync(Thing);
        Assert.Single(families);
        Assert.Equal(second.Families[0].FamilyId, families[0].FamilyId);
        ContentFamily next = await replaced.CreateFamilyAsync(Thing, "after", 16, Actor, Operator);
        Assert.Equal(second.Families[0].FamilyId + 1, next.FamilyId);

        // The point of the whole exercise: the replaced store serves the SAME bytes a store that had never
        // held anything would serve for this bundle, so the connect door's version and hash both match.
        using var never = new TemporaryCatalogDatabase();
        using var untouched = new SqliteContentAuthoringStore(
            never.ConnectionString, Registry(), never.Pack());
        await untouched.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        ContentPublishResult reference = await untouched.ImportBundleAsync(
            second, Actor, Operator, "second seed");

        Assert.Equal(reference.VersionNumber, imported.VersionNumber);
        Assert.Equal(reference.ServerManifestHash, imported.ServerManifestHash);
        Assert.Equal(reference.ClientManifestHash, imported.ClientManifestHash);
    }

    [Fact]
    public async Task AResetIsRefusedWhileADraftIsOpenAndForceTakesItAnyway()
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await Seed(store, "one", "two");
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("pending"), PublishFixtures.Fields(44))],
                Actor,
                Operator,
                "unpublished work");
            epochBefore = await store.GetStoreEpochAsync();
        }

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release"));

        Assert.Equal("draft-open", refused.Reason);
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_draft;"));
        Assert.Equal(epochBefore, Text(database, "SELECT store_epoch FROM catalog_metadata;"));

        ContentCatalogResetResult forced = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release", force: true);

        Assert.Equal(1, forced.ActiveVersion);
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_draft;"));
    }

    [Fact]
    public async Task AResetRefusedAfterTheDropLeavesTheCatalogExactlyAsItWas()
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await Seed(store, "one", "two");
            epochBefore = await store.GetStoreEpochAsync();
        }

        long auditsBefore = database.Scalar("SELECT COUNT(*) FROM catalog_audit;");

        // A host table of the file's own, carrying a row that points at the catalog version the store serves.
        // The reset defers foreign keys, so the implicit delete behind DROP TABLE catalog_version does not
        // fail there: the violation is counted and checked at COMMIT, which is the last thing the reset does,
        // after every table has been dropped, the schema recreated and the audit row written. That is the one
        // moment the all-or-nothing rule can be observed from outside, and no argument check can pre-empt it
        // because nothing about the arguments is wrong.
        database.Execute(
            """
            CREATE TABLE host_release_log(
                id INTEGER NOT NULL PRIMARY KEY,
                version_number INTEGER NOT NULL REFERENCES catalog_version(version_number));
            """);
        database.Execute("INSERT INTO host_release_log(id, version_number) VALUES (1, 1);");

        SqliteException failed = await Assert.ThrowsAsync<SqliteException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release"));

        // Named, so this test cannot quietly start passing on a failure that happens BEFORE the drop.
        Assert.Equal(19, failed.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", failed.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(epochBefore, Text(database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_version;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(auditsBefore, database.Scalar("SELECT COUNT(*) FROM catalog_audit;"));

        // The host's own table and its row came back with everything else.
        Assert.Equal(1L, database.Scalar("SELECT version_number FROM host_release_log;"));

        // Not merely present: still the schema a production host opens under ValidateOnly.
        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());
    }

    /// <summary>Two published rows, which is the smallest store with a version, rows and an audit trail.</summary>
    static Task Seed(SqliteContentAuthoringStore store, params string[] keys)
        => SqliteCatalogResetHarness.Seed(store, keys);

    /// <summary>One bundle exported from its own throwaway database, carrying a family and the named rows.</summary>
    static async Task<ContentBundle> Bundle(string familyKey, params string[] keys)
    {
        using var source = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            source.ConnectionString, Registry(), source.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.CreateFamilyAsync(Thing, familyKey, 16, Actor, Operator);
        await Seed(store, keys);
        return await store.ExportBundleAsync(1);
    }

    /// <summary>Every catalog table the database holds, by the schema's own inventory.</summary>
    static IReadOnlyList<string> Tables(TemporaryCatalogDatabase database)
        => SqliteCatalogResetHarness.Tables(database);

    static int CountTables(TemporaryCatalogDatabase database) => Tables(database).Count;

    /// <summary>One text scalar on a raw connection, beside the fixture's numeric one.</summary>
    static string Text(TemporaryCatalogDatabase database, string sql)
        => SqliteCatalogResetHarness.Text(database, sql);

    /// <summary>The ids the bundle NAMES, which an import is required to reproduce exactly.</summary>
    static int[] BundleIds(ContentBundle bundle)
    {
        var ids = new int[bundle.Rows.Count];
        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ids[i] = bundle.Rows[i].Id ?? 0;
        }

        return ids;
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
}
