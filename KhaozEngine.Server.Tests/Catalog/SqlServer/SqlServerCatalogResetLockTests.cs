using System;
using System.Data;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The reset behind the schema's application lock, which is the create's own lock on the create's own
/// resource name.
/// <para>
/// <b>There is no race to lose here.</b> The fact waits until the server itself reports a session WAITING
/// for this database's exclusive application lock, which only the reset can be. From that moment the reset
/// is parked on its first statement for as long as the holder keeps the lock, so asserting that it has not
/// finished and that the catalog is still whole is a fact rather than a timing guess. Releasing the lock
/// lets it through. The wait for the waiter is bounded only so a reset that never asks for the lock fails
/// here instead of hanging.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetLockTests
{
    /// <summary>The longest the fact waits for the reset to show up as a waiter on the lock.</summary>
    static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(30);

    [CatalogSqlServerFact]
    public async Task AResetWaitsBehindTheLockTheSchemaCreateTakes()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");

        await using var holder = new SqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await TakeAsync(holder, held) >= 0, "The holder must get the lock before the reset runs.");

        Task<ContentCatalogResetResult> reset = SqlServerCatalogReset.ResetAsync(
            database.ConnectionString,
            SqlServerCatalogResetHarness.Actor,
            SqlServerCatalogResetHarness.Operator,
            "content release");

        Assert.True(
            await WaitForLockWaiterAsync(database, reset),
            "The reset never showed up waiting on the schema's application lock.");

        // Not merely unfinished. The lock is the reset's first statement, so the catalog it would have
        // replaced is untouched too.
        Assert.False(reset.IsCompleted);
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));

        await held.RollbackAsync();

        ContentCatalogResetResult done = await reset;
        Assert.Equal(1, done.ActiveVersion);
        Assert.Equal(0, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
    }

    /// <summary>
    /// Whether a session is WAITING for an exclusive application lock in this database, polled until one is or
    /// the reset has already finished, which would mean it never waited. The collection is serialized, so the
    /// reset is the only session that can be waiting.
    /// </summary>
    static async Task<bool> WaitForLockWaiterAsync(SqlServerCatalogDatabase database, Task reset)
    {
        var bound = System.Diagnostics.Stopwatch.StartNew();
        while (bound.Elapsed < WaiterBound && !reset.IsCompleted)
        {
            int waiting = database.Scalar(
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_mode = N'X' AND request_status = N'WAIT'
                  AND resource_database_id = DB_ID();
                """);
            if (waiting > 0)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    /// <summary>
    /// The same lock, on the same resource name the provider names, taken with no wait at all so a failure to
    /// get it is reported here rather than hanging the test.
    /// </summary>
    static async Task<int> TakeAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
            SqlServerCatalogSchemaValidation.LockResource;
        return await command.ExecuteScalarAsync() is int code ? code : -999;
    }
}
