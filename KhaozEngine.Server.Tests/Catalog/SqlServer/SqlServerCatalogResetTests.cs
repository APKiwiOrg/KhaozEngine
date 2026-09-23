using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The catalog RESET against a live instance, mirroring <c>SqliteCatalogResetTests</c> fact for fact: what it
/// leaves behind, what it refuses, and the property the whole feature exists for, which is that a bundle
/// imported after a reset publishes as version 1 with the bundle's own ids and a manifest byte-identical to
/// the one a never-used store would produce.
/// <para>
/// Two facts here are load bearing and each has a test whose only job is to hold it. The reset is ALL OR
/// NOTHING, because a half-dropped catalog refuses the next open outright: the initializer creates only when
/// it counts zero catalog tables and validates every object by name otherwise. And the reset drops EVERY
/// table, because a table left standing keeps both its rows and its IDENTITY mark, and the mark is what makes
/// a later family id land above the bundle's.
/// </para>
/// <para>
/// One database rather than several, which is the shape the rest of this suite already runs in: the fixture's
/// isolation unit is the <c>dbo.catalog_*</c> schema of the one test instance, so a test that needs a store
/// that has never held anything drops the schema and creates it again, which is the same journey as a second
/// database.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetTests
{
    const string Actor = SqlServerCatalogResetHarness.Actor;
    const string Operator = SqlServerCatalogResetHarness.Operator;

    static ContentTypeId Thing => SqlServerCatalogResetHarness.Thing;

    static ContentTypeRegistry Registry() => SqlServerCatalogResetHarness.Registry();

    static ContentPublishRequest Request(int expectedBaseVersion)
        => SqlServerCatalogResetHarness.Request(expectedBaseVersion);

    static Task<SqlServerContentAuthoringStore> OpenAsync(SqlServerCatalogDatabase database)
        => SqlServerCatalogResetHarness.OpenAsync(database);

    [CatalogSqlServerFact]
    public async Task AResetLeavesASchemaTheInitializerValidatesAndACatalogThatPublishedNothing()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await Seed(store, "one", "two");
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("three"), CatalogFixtures.Fields(33))],
            Actor,
            Operator,
            "one more");
        await store.PublishAsync(Request(1));
        string epochBefore = await store.GetStoreEpochAsync();

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
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

        // ValidateOnly is the production mode and it compares every object name against the DDL, so this is
        // the recreate being checked by the same code a server boot would check it with.
        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(SqlServerCatalogSchema.CurrentVersion, await reopened.GetSchemaVersionAsync());
        Assert.Equal(SqlServerCatalogSchema.CurrentVersion, reset.SchemaVersion);
        Assert.Equal(SqlServerCatalogSchema.CurrentVersion, reset.PriorSchemaVersion);
        Assert.Equal(0, await reopened.GetActiveVersionAsync());
        Assert.Null(await reopened.GetPinnedVersionAsync());
        Assert.Equal(reset.StoreEpoch, await reopened.GetStoreEpochAsync());

        // The table count is read off a store that has only ever been created rather than written here as a
        // number, so a schema that gains a table does not quietly move what this compares against. The count
        // does NOT prove the drop skipped nothing. On this backend a skipped table fails the recreate's CREATE
        // TABLE and with it the whole reset, and the ROW counts are in
        // AResetDropsEveryTableSoNoRowAndNoIdentityMarkSurvivesIt.
        int afterReset = SqlServerCatalogResetHarness.CountTables(database);
        database.DropSchema();
        await OpenAsync(database);
        Assert.Equal(SqlServerCatalogResetHarness.CountTables(database), afterReset);
    }

    [CatalogSqlServerFact]
    public async Task AResetReportsTheActiveVersionAndNotAPinBelowIt()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await Seed(store, "one", "two");
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("three"), CatalogFixtures.Fields(33))],
            Actor,
            Operator,
            "one more");
        await store.PublishAsync(Request(1));
        await store.SetPinnedVersionAsync(1, Actor, Operator);
        ContentVersionRecord active = (await store.GetVersionAsync(2))!;

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        // A boot would have served the pin, version 1. The result names the active version and says so.
        Assert.Equal(2, reset.ActiveVersion);
        Assert.Equal(active.ServerManifestHash, reset.ServerManifestHash);
        Assert.Equal(active.ClientManifestHash, reset.ClientManifestHash);
        Assert.Contains(
            "active version 2, server manifest " + active.ServerManifestHash, reset.Summary, StringComparison.Ordinal);

        // The result is not the only place the hashes outlive the drop: the reset's own audit row files them.
        string filed = Text(database, "SELECT before_value FROM dbo.catalog_audit WHERE action = N'reset';");
        Assert.Contains(active.ServerManifestHash, filed, StringComparison.Ordinal);
        Assert.Contains(active.ClientManifestHash, filed, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task AResetDropsEveryTableSoNoRowAndNoIdentityMarkSurvivesIt()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await store.CreateFamilyAsync(Thing, "swords", 16, Actor, Operator);
        await Seed(store, "one", "two");

        await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        // Every catalog table, read back by NAME from the recreated database rather than listed here, so a
        // table the drop skipped and that held rows is caught by the same loop. A skipped table fails the
        // reset before this anyway, because the script's CREATE TABLE refuses a table that is still there.
        foreach (string table in Tables(database))
        {
            int expected = table switch
            {
                "catalog_metadata" => 1,
                "catalog_audit" => 1,
                _ => 0,
            };
            Assert.Equal(expected, database.Scalar("SELECT COUNT(*) FROM dbo." + table + ";"));
        }

        // The identity state restarted with the tables. A DELETE-based reset would leave the audit mark high
        // and hand this row an id in the hundreds, and would do the same to the next family id.
        Assert.Equal(1, database.Scalar("SELECT CAST(audit_id AS int) FROM dbo.catalog_audit;"));
        Assert.Equal(0, database.Scalar(
            "SELECT COUNT(*) FROM sys.identity_columns WHERE OBJECT_NAME(object_id) = N'catalog_family' AND last_value IS NOT NULL;"));

        // catalog_audit went with everything else, so the reset records ITSELF in the new store. The schema
        // takes the row as it stands: no version is attached and nothing about the table had to bend.
        Assert.Equal("reset", Text(database, "SELECT action FROM dbo.catalog_audit;"));
        Assert.Equal(Actor, Text(database, "SELECT actor FROM dbo.catalog_audit;"));
        Assert.Equal("content release", Text(database, "SELECT note FROM dbo.catalog_audit;"));
        Assert.Equal(0, database.Scalar("SELECT version_number FROM dbo.catalog_audit;"));
        Assert.Contains(
            "version 1",
            Text(database, "SELECT before_value FROM dbo.catalog_audit;"),
            StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task AfterAResetABundleImportsAsVersionOneWithItsOwnIdsAndAFreshStoresManifest()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentBundle first = await Bundle(database, "alpha_family", "one", "two");
        ContentBundle second = await Bundle(database, "beta_family", "left", "middle", "right");

        // The store is taken PAST version 1 and past the bundle's family ids, which is the state a content
        // release finds and the state ImportBundleAsync refuses outright.
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await store.ImportBundleAsync(first, Actor, Operator, "first seed");
        await store.CreateFamilyAsync(Thing, "later_one", 16, Actor, Operator);
        await store.CreateFamilyAsync(Thing, "later_two", 16, Actor, Operator);
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("extra"), CatalogFixtures.Fields(99))],
            Actor,
            Operator,
            "extra");
        await store.PublishAsync(Request(1));
        Assert.Equal(2, await store.GetActiveVersionAsync());

        ContentAuthoringException notEmpty = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(second, Actor, Operator, "second seed"));
        Assert.Equal("catalog-not-empty", notEmpty.Reason);

        await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        SqlServerContentAuthoringStore replaced = await OpenAsync(database);
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
        database.DropSchema();
        SqlServerContentAuthoringStore untouched = await OpenAsync(database);
        ContentPublishResult reference = await untouched.ImportBundleAsync(
            second, Actor, Operator, "second seed");

        Assert.Equal(reference.VersionNumber, imported.VersionNumber);
        Assert.Equal(reference.ServerManifestHash, imported.ServerManifestHash);
        Assert.Equal(reference.ClientManifestHash, imported.ClientManifestHash);
    }

    [CatalogSqlServerFact]
    public async Task AResetIsRefusedWhileADraftIsOpenAndForceTakesItAnyway()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await Seed(store, "one", "two");
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("pending"), CatalogFixtures.Fields(44))],
            Actor,
            Operator,
            "unpublished work");
        string epochBefore = await store.GetStoreEpochAsync();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release"));

        Assert.Equal("draft-open", refused.Reason);
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_draft;"));
        Assert.Equal(epochBefore, Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;"));

        ContentCatalogResetResult forced = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release", force: true);

        Assert.Equal(1, forced.ActiveVersion);
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_draft;"));
    }

    [CatalogSqlServerFact]
    public async Task AResetRefusedAfterTheDropLeavesTheCatalogExactlyAsItWas()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await Seed(store, "one", "two");
        string epochBefore = await store.GetStoreEpochAsync();
        int auditsBefore = database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit;");

        // A host table of the database's own, with a foreign key into the catalog version the store serves.
        // The reset drops the foreign keys it OWNS and then the tables, and this one is not its to drop, so
        // DROP TABLE dbo.catalog_version is refused by the engine with every owned foreign key already gone.
        // No argument check can pre-empt it, because nothing about the arguments is wrong.
        database.Execute(
            """
            CREATE TABLE dbo.host_release_log(
                id int NOT NULL PRIMARY KEY,
                version_number int NOT NULL
                    CONSTRAINT fk_host_release_log_version REFERENCES dbo.catalog_version(version_number));
            INSERT INTO dbo.host_release_log(id, version_number) VALUES (1, 1);
            """);

        try
        {
            SqlException failed = await Assert.ThrowsAsync<SqlException>(
                () => SqlServerCatalogReset.ResetAsync(
                    database.ConnectionString, Actor, Operator, "content release"));

            // Named, so this test cannot quietly start passing on a failure before the drop began.
            Assert.Equal(3726, failed.Number);

            Assert.Equal(epochBefore, Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;"));
            Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_version;"));
            Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
            Assert.Equal(auditsBefore, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit;"));

            // The host's own table and its row came back with everything else.
            Assert.Equal(1, database.Scalar("SELECT version_number FROM dbo.host_release_log;"));

            // Not merely present, and not merely the tables: ValidateOnly compares every foreign key by name,
            // and the reset had already dropped all thirteen of them when it was refused.
            var reopened = new SqlServerContentAuthoringStore(
                database.ConnectionString, Registry(), database.Pack());
            await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            Assert.Equal(1, await reopened.GetActiveVersionAsync());
        }
        finally
        {
            // The fixture's sweep cannot drop catalog_version while this stands, so every later test in the
            // collection would fail at its own setup.
            database.Execute("DROP TABLE IF EXISTS dbo.host_release_log;");
        }
    }

    /// <summary>Two published rows, which is the smallest store with a version, rows and an audit trail.</summary>
    static Task Seed(SqlServerContentAuthoringStore store, params string[] keys)
        => SqlServerCatalogResetHarness.Seed(store, keys);

    /// <summary>
    /// One bundle exported from a schema that is created for it and dropped after it, so the bundle carries
    /// its own ids rather than ids inherited from whatever ran before.
    /// </summary>
    static async Task<ContentBundle> Bundle(
        SqlServerCatalogDatabase database,
        string familyKey,
        params string[] keys)
    {
        database.DropSchema();
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await store.CreateFamilyAsync(Thing, familyKey, 16, Actor, Operator);
        await Seed(store, keys);
        ContentBundle bundle = await store.ExportBundleAsync(1);
        database.DropSchema();
        return bundle;
    }

    /// <summary>Every catalog table the database holds, by the schema's own inventory.</summary>
    static IReadOnlyList<string> Tables(SqlServerCatalogDatabase database)
        => SqlServerCatalogResetHarness.Tables(database);

    /// <summary>One text scalar on a raw connection, beside the fixture's numeric one.</summary>
    static string Text(SqlServerCatalogDatabase database, string sql)
        => SqlServerCatalogResetHarness.Text(database, sql);

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
