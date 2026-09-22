using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The reset against a file ANOTHER connection holds a write transaction on.
/// <para>
/// SQLite takes one writer at a time per database file, and the reset cannot drop a table without the write
/// lock. The other connection takes that lock before the reset is called and releases it only after the call
/// has returned, so this is not a race with a timing guess in it: the only outcome SQLite allows the reset is
/// the busy error once its own busy timeout runs out.
/// The fact pins what that outcome looks like afterwards: no partial catalog, nothing of the reset landed,
/// and the other connection's own write, committed after the refusal, is still there.
/// </para>
/// <para>
/// It proves nothing about HOW LONG the refusal takes. The reset's connection string names a one second busy
/// timeout only so the fact does not sit through the provider's default.
/// </para>
/// </summary>
public class SqliteCatalogResetContentionTests
{
    /// <summary>SQLITE_BUSY, which is what a second writer on one file gets.</summary>
    const int SqliteBusy = 5;

    [Fact]
    public async Task AResetWhileAnotherConnectionHoldsAWriteTransactionFailsBusyAndChangesNothing()
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

        long auditsBefore = database.Scalar("SELECT COUNT(*) FROM catalog_audit;");

        // A writer that has taken the file and is holding it, which is the shape of a host still running when
        // a release runner fires. BEGIN IMMEDIATE takes the write lock at once rather than at first write, and
        // the insert gives the writer a change of its own to commit afterwards.
        using var writer = new SqliteConnection(database.ConnectionString);
        writer.Open();
        Execute(writer, "BEGIN IMMEDIATE;");
        Execute(writer, "INSERT INTO catalog_audit(occurred_at_utc, actor, action) VALUES (1, 'writer', 'edit');");

        SqliteException busy = await Assert.ThrowsAsync<SqliteException>(
            () => SqliteCatalogReset.ResetAsync(
                database.ConnectionString + ";Default Timeout=1",
                SqliteCatalogResetHarness.Actor,
                SqliteCatalogResetHarness.Operator,
                "content release"));

        Assert.Equal(SqliteBusy, busy.SqliteErrorCode);

        // The writer was never disturbed: its transaction is still open and still commits.
        Execute(writer, "COMMIT;");
        SqliteConnection.ClearPool(writer);
        writer.Close();

        // Nothing of the catalog moved, down to the epoch a recreate would have replaced, and the reset's own
        // audit row is absent while the writer's is present.
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(auditsBefore + 1, database.Scalar("SELECT COUNT(*) FROM catalog_audit;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_audit WHERE actor = 'writer';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_audit WHERE action = 'reset';"));
        Assert.Equal(
            SqliteCatalogSchemaInventory.Tables.Count, SqliteCatalogResetHarness.Tables(database).Count);

        // Not a partial catalog: the production open validates every object by name.
        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());

        // And once the writer is gone the same call goes through and reports the catalog as the writer left it.
        ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
            database.ConnectionString + ";Default Timeout=1",
            SqliteCatalogResetHarness.Actor,
            SqliteCatalogResetHarness.Operator,
            "content release");
        Assert.Equal(ContentCatalogPriorState.Read, reset.PriorState);
        Assert.Equal(1, reset.ActiveVersion);
        Assert.Equal(2, reset.RowsDropped);
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
