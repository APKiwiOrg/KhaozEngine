using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The reset and the catalog SCHEMA version. The rule is that a readable version older than the build's is
/// reset like any other and comes back at the build's version, and a newer one is refused with nothing
/// dropped, forced or not. Version 2 added <c>catalog_content_upgrade</c>, so these are also the facts that
/// the ledger table is inside the reset's inventory.
/// <para>
/// The version 1 catalog here is a real store taken back to version 1's shape: the ledger table dropped and
/// the metadata row set to 1. The provider's own validator is asked to confirm that shape before each reset,
/// because it compares every version 1 object by name before it names the migration.
/// </para>
/// </summary>
public class SqliteCatalogResetSchemaVersionTests
{
    const string Actor = SqliteCatalogResetHarness.Actor;
    const string Operator = SqliteCatalogResetHarness.Operator;

    const int Current = SqliteCatalogSchema.CurrentVersion;

    [Fact]
    public async Task ALedgerRowGoesWithTheCatalogAndTheLedgerTableComesBackEmpty()
    {
        using var database = new TemporaryCatalogDatabase();
        using (SqliteContentAuthoringStore store = await SeedAsync(database))
        {
            await store.RecordUpgradeAsync(
                new ContentUpgradeStamp("harvest-profiles", 1), ContentUpgradeDisposition.Baseline, Actor, Operator);
        }

        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_content_upgrade;"));

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        Assert.Equal(Current, reset.PriorSchemaVersion);
        Assert.Equal(Current, reset.SchemaVersion);
        Assert.Contains("catalog_content_upgrade", SqliteCatalogResetHarness.Tables(database));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_content_upgrade;"));

        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
        Assert.Empty(await reopened.ListUpgradesAsync());
    }

    [Fact]
    public async Task AVersionOneCatalogIsResetAndComesBackAtTheBuildsVersion()
    {
        using var database = new TemporaryCatalogDatabase();
        (await SeedAsync(database)).Dispose();
        TakeBackToVersionOne(database);
        await AssertIsVersionOneAsync(database);

        // No force: an older catalog is a whole catalog, not a partial one.
        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
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

        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
        Assert.Equal(0, await reopened.GetActiveVersionAsync());
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_content_upgrade;"));
        Assert.Equal(
            SqliteCatalogSchemaInventory.Tables.Count, SqliteCatalogResetHarness.Tables(database).Count);
    }

    [Fact]
    public async Task APartialVersionOneCatalogIsRefusedWithoutForceAndRepairedToTheBuildsVersionWithIt()
    {
        using var database = new TemporaryCatalogDatabase();
        (await SeedAsync(database)).Dispose();
        TakeBackToVersionOne(database);
        database.Execute("DROP TABLE catalog_chunk;");
        int standing = SqliteCatalogResetHarness.Tables(database).Count;

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release"));
        Assert.Equal("catalog-partial", refused.Reason);
        Assert.Contains("schema version '1'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(standing, SqliteCatalogResetHarness.Tables(database).Count);
        Assert.Equal(1L, database.Scalar("SELECT schema_version FROM catalog_metadata;"));

        ContentCatalogResetResult repaired = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "repair", force: true);

        Assert.Equal(ContentCatalogPriorState.Unreadable, repaired.PriorState);
        Assert.Equal(Current, repaired.SchemaVersion);
        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(Current, await reopened.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task ANewerSchemaVersionIsRefusedAndNothingIsDroppedEvenWithForce()
    {
        using var database = new TemporaryCatalogDatabase();
        (await SeedAsync(database)).Dispose();
        string epochBefore = SqliteCatalogResetHarness.Text(database, "SELECT store_epoch FROM catalog_metadata;");
        database.Execute(FormattableString.Invariant(
            $"UPDATE catalog_metadata SET schema_version = {Current + 1} WHERE metadata_key = 1;"));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "content release", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains(FormattableString.Invariant($"version '{Current + 1}', newer"), refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was dropped", refused.Message, StringComparison.Ordinal);
        AssertUntouched(database, epochBefore, SqliteCatalogSchemaInventory.Tables.Count);
        Assert.Equal((long)Current + 1, database.Scalar("SELECT schema_version FROM catalog_metadata;"));
    }

    [Fact]
    public async Task APartialCatalogAtANewerSchemaVersionIsRefusedEvenWithForce()
    {
        using var database = new TemporaryCatalogDatabase();
        (await SeedAsync(database)).Dispose();
        string epochBefore = SqliteCatalogResetHarness.Text(database, "SELECT store_epoch FROM catalog_metadata;");
        database.Execute(FormattableString.Invariant(
            $"UPDATE catalog_metadata SET schema_version = {Current + 1} WHERE metadata_key = 1;"));
        database.Execute("DROP TABLE catalog_chunk;");

        // The version is still READABLE, so it still decides. Force repairs a partial catalog, and this one is
        // partial, but recreating an older schema over a database that says it is newer is not a repair.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString, Actor, Operator, "repair", force: true));

        Assert.Equal("schema-mismatch", refused.Reason);
        AssertUntouched(database, epochBefore, SqliteCatalogSchemaInventory.Tables.Count - 1);
    }

    /// <summary>A published store with one version, two rows and an audit trail, left open for the caller.</summary>
    static async Task<SqliteContentAuthoringStore> SeedAsync(TemporaryCatalogDatabase database)
    {
        var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await SqliteCatalogResetHarness.Seed(store, "one", "two");
        return store;
    }

    /// <summary>Version 2 undone: the one table it added gone and the metadata row back at 1.</summary>
    static void TakeBackToVersionOne(TemporaryCatalogDatabase database) => database.Execute(
        """
        DROP TABLE catalog_content_upgrade;
        UPDATE catalog_metadata SET schema_version = 1 WHERE metadata_key = 1;
        """);

    /// <summary>
    /// The provider's own verdict that this is version 1 and nothing else: ValidateOnly compares every
    /// version 1 object by name and only then refuses by naming the version.
    /// </summary>
    static async Task AssertIsVersionOneAsync(TemporaryCatalogDatabase database)
    {
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
        Assert.Contains("at unsupported version '1'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Every row and every table of the seeded catalog where it was, down to the epoch.</summary>
    static void AssertUntouched(TemporaryCatalogDatabase database, string epochBefore, int tables)
    {
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_version;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_audit WHERE action = 'reset';"));
        Assert.Equal(tables, SqliteCatalogResetHarness.Tables(database).Count);
    }
}
