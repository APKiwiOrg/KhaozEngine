using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Which provider errors before the drop the reset may call a SCHEMA problem. Only SQL errors 207 and 208, an
/// invalid column or object name, say anything about the schema. A deadlock victim, a timeout or a dropped
/// connection says nothing about it, and reporting one as <c>schema-mismatch</c> would send an operator to
/// apply a migration that cannot help.
/// <para>
/// <b>The deadlock is built, not hoped for.</b> The holder takes catalog_row exclusively and the fact waits
/// until the server reports the reset blocked on the holder while it already holds its lock on
/// catalog_metadata. Only then does the holder ask for catalog_metadata, which closes the cycle, and the holder
/// runs at a higher deadlock priority so the server picks the reset as the victim.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetProviderErrorTests
{
    /// <summary>The longest the fact waits for the reset to show up blocked on the holder.</summary>
    static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(207, true)]
    [InlineData(208, true)]
    [InlineData(1205, false)]
    [InlineData(-2, false)]
    [InlineData(3726, false)]
    public void OnlyAnInvalidColumnOrObjectNameIsASchemaError(int number, bool schema)
        => Assert.Equal(schema, SqlServerCatalogReset.IsMissingName(number));

    [CatalogSqlServerFact]
    public async Task ADeadlockBeforeTheDropIsRethrownRatherThanReportedAsASchemaMismatch()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");

        await using var holder = new SqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        int holderSession = await ScalarAsync(holder, held, "SET DEADLOCK_PRIORITY HIGH; SELECT CAST(@@SPID AS int);");
        await ScalarAsync(holder, held, "SELECT COUNT(*) FROM dbo.catalog_row WITH (TABLOCKX, HOLDLOCK);");

        Task<ContentCatalogResetResult> reset = SqlServerCatalogReset.ResetAsync(
            database.ConnectionString,
            SqlServerCatalogResetHarness.Actor,
            SqlServerCatalogResetHarness.Operator,
            "content release");

        Assert.True(
            await WaitForCycleAsync(database, holderSession, reset),
            "The reset never showed up blocked on the holder while holding catalog_metadata.");

        Task<int> closing = ScalarAsync(
            holder, held, "SELECT COUNT(*) FROM dbo.catalog_metadata WITH (TABLOCKX, HOLDLOCK);");

        Exception? thrown = await Record.ExceptionAsync(() => reset);
        await closing;
        await held.RollbackAsync();

        SqlException failed = Assert.IsType<SqlException>(thrown);
        Assert.Equal(1205, failed.Number);

        // Nothing was dropped: the victim's transaction rolled back with its lock, before its first drop.
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit WHERE action = N'reset';"));
    }

    /// <summary>
    /// Whether a session other than the holder is WAITING on the holder while it holds a granted lock on
    /// catalog_metadata, which is the reset parked on catalog_row with half the cycle already in place.
    /// </summary>
    static async Task<bool> WaitForCycleAsync(SqlServerCatalogDatabase database, int holderSession, Task reset)
    {
        string sql = FormattableString.Invariant(
            $"""
            SELECT COUNT(*)
            FROM sys.dm_os_waiting_tasks AS w
            JOIN sys.dm_tran_locks AS l ON l.request_session_id = w.session_id
            WHERE w.blocking_session_id = {holderSession}
              AND l.resource_type = N'OBJECT' AND l.request_status = N'GRANT'
              AND l.resource_database_id = DB_ID()
              AND l.resource_associated_entity_id = OBJECT_ID(N'dbo.catalog_metadata');
            """);
        var bound = System.Diagnostics.Stopwatch.StartNew();
        while (bound.Elapsed < WaiterBound && !reset.IsCompleted)
        {
            if (database.Scalar(sql) > 0)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() is int value ? value : -1;
    }
}
