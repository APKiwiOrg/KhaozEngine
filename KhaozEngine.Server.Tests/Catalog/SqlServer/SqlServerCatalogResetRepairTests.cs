using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The two databases that are not a whole catalog, mirroring <c>SqliteCatalogResetRepairTests</c> fact for
/// fact: one carrying NONE of the schema's tables, and one carrying some of them.
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetRepairTests
{
    const string Actor = SqlServerCatalogResetHarness.Actor;
    const string Operator = SqlServerCatalogResetHarness.Operator;

    /// <summary>How many tables a half-finished deletion leaves standing below.</summary>
    const int Standing = 11;

    [CatalogSqlServerFact]
    public async Task ADatabaseCarryingNoCatalogTableIsCreatedRatherThanRefused()
    {
        using var database = new SqlServerCatalogDatabase();

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "first release");

        Assert.Equal(ContentCatalogPriorState.Absent, reset.PriorState);
        Assert.Equal(0, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Null(reset.ClientManifestHash);
        Assert.Equal(0, reset.VersionsDropped);
        Assert.Equal(0, reset.RowsDropped);
        Assert.Equal(32, reset.StoreEpoch.Length);
        Assert.Contains("no catalog at all", reset.Summary, StringComparison.Ordinal);

        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Equal(reset.StoreEpoch, await store.GetStoreEpochAsync());
        Assert.Equal(
            "reset",
            SqlServerCatalogResetHarness.Text(database, "SELECT action FROM dbo.catalog_audit;"));
    }

    [CatalogSqlServerFact]
    public async Task AResetThenAnImportIsTheWholeFirstReleaseAgainstANewDatabase()
    {
        using var database = new SqlServerCatalogDatabase();
        await SqlServerCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "first release");

        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");

        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [CatalogSqlServerFact]
    public async Task APartialCatalogIsRefusedWithoutForceAndNothingChanges()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        string epochBefore = SqlServerCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM dbo.catalog_metadata;");
        Halve(database);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release"));

        Assert.Equal("catalog-partial", refused.Reason);
        Assert.Contains("force", refused.Message, StringComparison.Ordinal);

        Assert.Equal(epochBefore, SqlServerCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM dbo.catalog_metadata;"));
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
        Assert.Equal(Standing, SqlServerCatalogResetHarness.CountTables(database));
    }

    [CatalogSqlServerFact]
    public async Task ForceRepairsAPartialCatalogAndSaysItCouldNotReadWhatStood()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        Halve(database);

        // The state this repairs is one no store can open at all, which is what makes the reset the only way
        // out of it.
        var broken = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        ContentAuthoringException cannotOpen = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => broken.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
        Assert.Equal("schema-mismatch", cannotOpen.Reason);

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "repair", force: true);

        Assert.Equal(ContentCatalogPriorState.Unreadable, reset.PriorState);
        Assert.Equal(0, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Equal(0, reset.VersionsDropped);
        Assert.Equal(0, reset.RowsDropped);
        Assert.Contains("PARTIAL", reset.Summary, StringComparison.Ordinal);

        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Equal(reset.StoreEpoch, await store.GetStoreEpochAsync());
    }

    [CatalogSqlServerFact]
    public async Task ASchemaVersionThisBuildDoesNotWriteIsRefusedEvenWithForce()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE dbo.catalog_metadata SET schema_version = 2 WHERE metadata_key = 1;");

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("unsupported version '2'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
    }

    [CatalogSqlServerFact]
    public async Task APartialCatalogAtAnotherSchemaVersionIsRefusedEvenWithForce()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE dbo.catalog_metadata SET schema_version = 2 WHERE metadata_key = 1;");
        Halve(database);

        // The version is still READABLE, so it still decides. Recreating version 1 over a database that says
        // it is at version 2 would not be a repair.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqlServerCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "repair", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Equal(Standing, SqlServerCatalogResetHarness.CountTables(database));
    }

    [CatalogSqlServerFact]
    public async Task AnActiveVersionNamingARowThatIsNotThereIsNotReadAsNothingPublished()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);
        database.Execute("UPDATE dbo.catalog_metadata SET active_version = 9 WHERE metadata_key = 1;");

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        Assert.Equal(ContentCatalogPriorState.Read, reset.PriorState);
        Assert.Equal(9, reset.ActiveVersion);
        Assert.Null(reset.ServerManifestHash);
        Assert.Null(reset.ClientManifestHash);
        Assert.Contains("version 9, whose version row was MISSING", reset.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing published", reset.Summary, StringComparison.Ordinal);
    }

    /// <summary>A published store with a version, two rows and an audit trail.</summary>
    static async Task SeedAsync(SqlServerCatalogDatabase database)
    {
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
    }

    /// <summary>
    /// A half-finished manual deletion: three of the schema's tables gone and eleven standing. Each of the
    /// three is a leaf, so the foreign keys pointing into the rest do not have to be unpicked first, which is
    /// exactly how far an operator with a SQL prompt would have got.
    /// </summary>
    static void Halve(SqlServerCatalogDatabase database) => database.Execute(
        """
        DROP TABLE dbo.catalog_chunk;
        DROP TABLE dbo.catalog_draft_edit_field;
        DROP TABLE dbo.catalog_draft_edit;
        """);
}
