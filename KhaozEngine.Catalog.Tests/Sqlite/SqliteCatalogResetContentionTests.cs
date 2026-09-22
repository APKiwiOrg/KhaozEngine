using System;
using System.Diagnostics;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The reset against a file ANOTHER connection is writing to.
/// <para>
/// SQLite takes one writer at a time per database file, and the reset is a writer that wants every table in
/// it. There is no queue and no partial outcome: it waits for the busy timeout its connection string names
/// and then fails with the provider's busy error, having changed nothing. The remedy is an operator one, stop
/// the writers, so the useful behaviour is failing QUICKLY under a short timeout rather than blocking a
/// release runner for the provider's default.
/// </para>
/// </summary>
public class SqliteCatalogResetContentionTests
{
    /// <summary>SQLITE_BUSY, which is what a second writer on one file gets.</summary>
    const int SqliteBusy = 5;

    /// <summary>The seconds the reset's own connection string gives the file.</summary>
    const int TimeoutSeconds = 1;

    [Fact]
    public async Task AResetIsRefusedWhileAnotherConnectionHoldsAWriteTransaction()
    {
        using var database = new TemporaryCatalogDatabase();
        string epochBefore;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await SqliteCatalogResetHarness.Seed(store, "one", "two");
            epochBefore = await store.GetStoreEpochAsync();
        }

        // A writer that has taken the file and is holding it, which is the shape of a host still running when
        // a release runner fires. BEGIN IMMEDIATE takes the reserved lock at once rather than at first write.
        using var writer = new SqliteConnection(database.ConnectionString);
        writer.Open();
        Execute(writer, "BEGIN IMMEDIATE;");
        Execute(writer, "INSERT INTO catalog_audit(occurred_at_utc, actor, action) VALUES (1, 'writer', 'edit');");

        var elapsed = Stopwatch.StartNew();
        SqliteException busy = await Assert.ThrowsAsync<SqliteException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString + ";Default Timeout=" + TimeoutSeconds,
                SqliteCatalogResetHarness.Actor,
                SqliteCatalogResetHarness.Operator,
                "content release"));
        elapsed.Stop();

        Assert.Equal(SqliteBusy, busy.SqliteErrorCode);

        // Inside the timeout it was given rather than the provider's default, which is what makes a short one
        // worth setting. The bound is loose because a loaded machine is still allowed to be slow.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(20),
            "The reset must fail inside the busy timeout its connection string names, not block on the file.");

        Execute(writer, "ROLLBACK;");
        SqliteConnection.ClearPool(writer);
        writer.Close();

        // Nothing of the catalog moved, down to the epoch a recreate would have replaced.
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(
            SqliteCatalogSchemaInventory.Tables.Count, SqliteCatalogResetHarness.Tables(database).Count);

        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
