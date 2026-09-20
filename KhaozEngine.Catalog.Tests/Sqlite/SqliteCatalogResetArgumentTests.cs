using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The arguments the audit row's own CHECK constraints will refuse, refused BEFORE the transaction opens.
/// <para>
/// <b>The order is the whole point.</b> The audit insert is the last statement of the reset, so an actor the
/// column cannot hold used to drop every table, recreate the schema, and only then fail, with a raw provider
/// exception and a rollback. The catalog came back, but the caller was handed a
/// <c>SqliteException</c> for a mistake in its own argument list. These are
/// <c>ArgumentException</c> now, and nothing is opened, read or dropped on the way to them.
/// </para>
/// </summary>
public class SqliteCatalogResetArgumentTests
{
    [Theory]
    [InlineData("", "oid:tests", "note")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "oid:tests", "note")]
    [InlineData("actor", "ooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo", "note")]
    public async Task AnArgumentTheAuditColumnCannotHoldIsRefusedWithNothingDropped(
        string actor,
        string operatorId,
        string note)
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore = await SeedAsync(database);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, actor, operatorId, note));

        await AssertUntouched(database, epochBefore);
    }

    [Fact]
    public async Task AnOverLongNoteIsRefusedWithNothingDropped()
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore = await SeedAsync(database);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString,
                SqliteCatalogResetHarness.Actor,
                SqliteCatalogResetHarness.Operator,
                new string('n', 1025)));

        await AssertUntouched(database, epochBefore);
    }

    [Fact]
    public async Task EveryArgumentAtItsColumnsLimitIsAccepted()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);

        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString,
            new string('a', 128),
            new string('o', 128),
            new string('n', 1024));

        Assert.Equal(1, reset.ActiveVersion);
        Assert.Equal(128L, database.Scalar("SELECT length(actor) FROM catalog_audit;"));
        Assert.Equal(128L, database.Scalar("SELECT length(operator) FROM catalog_audit;"));
        Assert.Equal(1024L, database.Scalar("SELECT length(note) FROM catalog_audit;"));

        // The summary is what goes into before_value, whose CHECK caps it at 4096. It is a fixed sentence
        // around two 64-character hashes, a 32-character epoch and three numbers, so it cannot approach that.
        Assert.True(
            database.Scalar("SELECT length(before_value) FROM catalog_audit;") < 4096L,
            "The reset summary must stay well under the audit column's 4096 cap.");
    }

    [Fact]
    public async Task ANullArgumentIsStillANullArgument()
    {
        using var database = new TemporaryCatalogDatabase();
        await SeedAsync(database);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, null!, "oid", "note"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", null!, "note"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", "oid", null!));
    }

    /// <summary>A published store with a version, rows and an epoch to compare against.</summary>
    static async Task<string> SeedAsync(TemporaryCatalogDatabase database)
    {
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await SqliteCatalogResetHarness.Seed(store, "one", "two");
        return await store.GetStoreEpochAsync();
    }

    /// <summary>The store exactly as it stood, down to the epoch a recreate would have replaced.</summary>
    static async Task AssertUntouched(TemporaryCatalogDatabase database, string epochBefore)
    {
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));

        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());
    }
}
