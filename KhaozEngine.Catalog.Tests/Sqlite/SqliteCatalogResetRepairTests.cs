using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The two databases that are not a whole catalog: one carrying NONE of the schema's tables, and one
/// carrying some of them.
/// <para>
/// <b>An empty database is not an error.</b> The scripted release path is reset then import, so the first
/// release against a new database arrives here, and refusing it for a migration that exists only as this
/// provider's own script would mean a release that cannot run once.
/// </para>
/// <para>
/// <b>A partial catalog is a half-finished manual deletion.</b> No store can open it and no read can describe
/// what it holds, so without <c>force</c> it is refused with the reason token and the sentence that name the
/// remedy, and with <c>force</c> the reset repairs it by dropping what is left and recreating the schema.
/// </para>
/// </summary>
public class SqliteCatalogResetRepairTests
{
    const string Actor = SqliteCatalogResetHarness.Actor;
    const string Operator = SqliteCatalogResetHarness.Operator;

    [Fact]
    public async Task ADatabaseCarryingNoCatalogTableIsCreatedRatherThanRefused()
    {
        using var database = new TemporaryCatalogDatabase();
        database.Execute("CREATE TABLE host_own_table(id INTEGER NOT NULL PRIMARY KEY);");

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "first release");

        Assert.Equal(ContentCatalogPriorState.Absent, reset.PriorState);
        Assert.Equal(0, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Null(reset.ClientManifestHash);
        Assert.Equal(0, reset.VersionsDropped);
        Assert.Equal(0, reset.RowsDropped);
        Assert.Equal(32, reset.StoreEpoch.Length);
        Assert.Contains("no catalog at all", reset.Summary, StringComparison.Ordinal);

        // The same schema the initializer would have created, checked by the same code a server boot checks
        // it with, and the audit row that says how it got here.
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Equal(reset.StoreEpoch, await store.GetStoreEpochAsync());
        Assert.Equal("reset", SqliteCatalogResetHarness.Text(database, "SELECT action FROM catalog_audit;"));

        // And the host's own table was not the reset's business on the way in either.
        Assert.Equal(1L, database.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'host_own_table';"));
    }

    [Fact]
    public async Task AResetThenAnImportIsTheWholeFirstReleaseAgainstANewDatabase()
    {
        using var database = new TemporaryCatalogDatabase();
        await SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "first release");

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await SqliteCatalogResetHarness.Seed(store, "one", "two");

        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task APartialCatalogIsRefusedWithoutForceAndNothingChanges()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);
        string epochBefore = SqliteCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM catalog_metadata;");
        Halve(database);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release"));

        Assert.Equal("catalog-partial", refused.Reason);
        Assert.Contains("force", refused.Message, StringComparison.Ordinal);

        // What was left of the catalog is exactly as it was, rows included, and the audit is untouched.
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(11, SqliteCatalogResetHarness.Tables(database).Count);
    }

    [Fact]
    public async Task ForceRepairsAPartialCatalogAndSaysItCouldNotReadWhatStood()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);
        Halve(database);

        // The state this repairs is one no store can open at all, which is what makes the reset the only way
        // out of it.
        using (var broken = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack()))
        {
            ContentAuthoringException cannotOpen = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => broken.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
            Assert.Equal("schema-mismatch", cannotOpen.Reason);
        }

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "repair", force: true);

        Assert.Equal(ContentCatalogPriorState.Unreadable, reset.PriorState);
        Assert.Equal(0, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Equal(0, reset.VersionsDropped);
        Assert.Equal(0, reset.RowsDropped);
        Assert.Contains("PARTIAL", reset.Summary, StringComparison.Ordinal);

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Equal(reset.StoreEpoch, await store.GetStoreEpochAsync());
        Assert.Equal(
            SqliteCatalogSchemaInventory.Tables.Count, SqliteCatalogResetHarness.Tables(database).Count);
    }

    [Fact]
    public async Task ASchemaVersionThisBuildDoesNotWriteIsRefusedEvenWithForce()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE catalog_metadata SET schema_version = 2 WHERE metadata_key = 1;");

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("unsupported version '2'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
    }

    [Fact]
    public async Task APartialCatalogAtAnotherSchemaVersionIsRefusedEvenWithForce()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE catalog_metadata SET schema_version = 2 WHERE metadata_key = 1;");
        Halve(database);

        // The version is still READABLE, so it still decides. Recreating version 1 over a database that says
        // it is at version 2 would not be a repair.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "repair", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Equal(11, SqliteCatalogResetHarness.Tables(database).Count);
    }

    [Fact]
    public async Task AnActiveVersionNamingARowThatIsNotThereIsNotReadAsNothingPublished()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE catalog_metadata SET active_version = 9 WHERE metadata_key = 1;");

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        // The number keeps its meaning and the sentence says the row behind it was gone. Reporting "nothing
        // published" beside the number 9 would have been two answers to one question.
        Assert.Equal(ContentCatalogPriorState.Read, reset.PriorState);
        Assert.Equal(9, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Null(reset.ClientManifestHash);
        Assert.Contains("version 9, whose version row was MISSING", reset.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing published", reset.Summary, StringComparison.Ordinal);
    }

    /// <summary>A published store with a version, two rows and an audit trail.</summary>
    static async Task SeedAsync(TemporaryCatalogDatabase database)
    {
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await SqliteCatalogResetHarness.Seed(store, "one", "two");
    }

    /// <summary>
    /// A half-finished manual deletion: three of the schema's tables gone and eleven standing. The raw
    /// connection runs without the store's bootstrap pragma, so foreign keys are off and the order is free,
    /// which is exactly how an operator with a SQL prompt would have got here.
    /// </summary>
    static void Halve(TemporaryCatalogDatabase database) => database.Execute(
        """
        DROP TABLE catalog_chunk;
        DROP TABLE catalog_draft_edit_field;
        DROP TABLE catalog_draft_edit;
        """);
}
