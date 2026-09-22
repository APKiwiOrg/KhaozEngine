using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The reset and the catalog SCHEMA version, mirroring <c>SqliteCatalogResetSchemaVersionTests</c> fact for
/// fact. A readable version older than the build's is reset like any other and comes back at the build's
/// version, and a newer one is refused with nothing dropped, forced or not. Version 2 added
/// <c>catalog_content_upgrade</c>, so these are also the facts that the ledger table is inside the reset's
/// inventory.
/// <para>
/// The version 1 catalog here is a real store taken back to version 1's shape: the ledger table dropped and
/// the metadata row set to 1. The provider's own validator is asked to confirm that shape before each reset,
/// because it compares every version 1 object by name before it names the migration.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetSchemaVersionTests
{
    const string Actor = SqlServerCatalogResetHarness.Actor;
    const string Operator = SqlServerCatalogResetHarness.Operator;

    const int Current = SqlServerCatalogSchema.CurrentVersion;

    [CatalogSqlServerFact]
    public async Task ALedgerRowGoesWithTheCatalogAndTheLedgerTableComesBackEmpty()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SeedAsync(database);
        await store.RecordUpgradeAsync(
            new ContentUpgradeStamp("harvest-profiles", 1), ContentUpgradeDisposition.Baseline, Actor, Operator);
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_content_upgrade;"));

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        Assert.Equal(Current, reset.PriorSchemaVersion);
        Assert.Equal(Current, reset.SchemaVersion);
        Assert.Contains("catalog_content_upgrade", SqlServerCatalogResetHarness.Tables(database));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_content_upgrade;"));

        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
        Assert.Empty(await reopened.ListUpgradesAsync());
    }

    [CatalogSqlServerFact]
    public async Task AVersionOneCatalogIsResetAndComesBackAtTheBuildsVersion()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        TakeBackToVersionOne(database);
        await AssertIsVersionOneAsync(database);

        // No force: an older catalog is a whole catalog, not a partial one.
        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        Assert.Equal(ContentCatalogPriorState.Read, reset.PriorState);
        Assert.Equal(1, reset.PriorSchemaVersion);
        Assert.Equal(Current, reset.SchemaVersion);
        Assert.Equal(1, reset.ActiveVersion);
        Assert.Equal(1, reset.VersionsDropped);
        Assert.Equal(2, reset.RowsDropped);
        Assert.Contains("on schema version 1", reset.Summary, StringComparison.Ordinal);
        Assert.Contains(
            FormattableString.Invariant($"The new store is at schema version {Current}"),
            reset.Summary,
            StringComparison.Ordinal);

        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
        Assert.Equal(0, await reopened.GetActiveVersionAsync());
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_content_upgrade;"));
        Assert.Equal(
            SqlServerCatalogSchemaExpectations.Tables.Count, SqlServerCatalogResetHarness.CountTables(database));
    }

    [CatalogSqlServerFact]
    public async Task APartialVersionOneCatalogIsRefusedWithoutForceAndRepairedToTheBuildsVersionWithIt()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        TakeBackToVersionOne(database);
        database.Execute("DROP TABLE dbo.catalog_chunk;");
        int standing = SqlServerCatalogResetHarness.CountTables(database);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release"));
        Assert.Equal("catalog-partial", refused.Reason);
        Assert.Contains("schema version '1'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(standing, SqlServerCatalogResetHarness.CountTables(database));
        Assert.Equal(1, database.Scalar("SELECT schema_version FROM dbo.catalog_metadata;"));

        ContentCatalogResetResult repaired = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "repair", force: true);

        Assert.Equal(ContentCatalogPriorState.Unreadable, repaired.PriorState);
        Assert.Equal(Current, repaired.SchemaVersion);
        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
    }

    [CatalogSqlServerFact]
    public async Task ANewerSchemaVersionIsRefusedAndNothingIsDroppedEvenWithForce()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        string epochBefore = SqlServerCatalogResetHarness.Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;");
        database.Execute(FormattableString.Invariant(
            $"UPDATE dbo.catalog_metadata SET schema_version = {Current + 1} WHERE metadata_key = 1;"));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains(FormattableString.Invariant($"version '{Current + 1}', newer"), refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was dropped", refused.Message, StringComparison.Ordinal);
        AssertUntouched(database, epochBefore, SqlServerCatalogSchemaExpectations.Tables.Count);
        Assert.Equal(Current + 1, database.Scalar("SELECT schema_version FROM dbo.catalog_metadata;"));
    }

    [CatalogSqlServerFact]
    public async Task APartialCatalogAtANewerSchemaVersionIsRefusedEvenWithForce()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        string epochBefore = SqlServerCatalogResetHarness.Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;");
        database.Execute(FormattableString.Invariant(
            $"UPDATE dbo.catalog_metadata SET schema_version = {Current + 1} WHERE metadata_key = 1;"));
        database.Execute("DROP TABLE dbo.catalog_chunk;");

        // The version is still READABLE, so it still decides. Force repairs a partial catalog, and this one is
        // partial, but recreating an older schema over a database that says it is newer is not a repair.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "repair", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        AssertUntouched(database, epochBefore, SqlServerCatalogSchemaExpectations.Tables.Count - 1);
    }

    /// <summary>A published store with one version, two rows and an audit trail.</summary>
    static async Task<SqlServerContentAuthoringStore> SeedAsync(SqlServerCatalogDatabase database)
    {
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
        return store;
    }

    /// <summary>Version 2 undone: the one table it added gone and the metadata row back at 1.</summary>
    static void TakeBackToVersionOne(SqlServerCatalogDatabase database) => database.Execute(
        """
        DROP TABLE dbo.catalog_content_upgrade;
        UPDATE dbo.catalog_metadata SET schema_version = 1 WHERE metadata_key = 1;
        """);

    /// <summary>
    /// The provider's own verdict that this is version 1 and nothing else: ValidateOnly compares every
    /// version 1 object by name and only then refuses by naming the version.
    /// </summary>
    static async Task AssertIsVersionOneAsync(SqlServerCatalogDatabase database)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
        Assert.Contains("at unsupported version '1'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Every row and every table of the seeded catalog where it was, down to the epoch.</summary>
    static void AssertUntouched(SqlServerCatalogDatabase database, string epochBefore, int tables)
    {
        Assert.Equal(epochBefore, SqlServerCatalogResetHarness.Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;"));
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_version;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit WHERE action = N'reset';"));
        Assert.Equal(tables, SqlServerCatalogResetHarness.CountTables(database));
    }
}
