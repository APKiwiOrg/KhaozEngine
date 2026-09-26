using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The SQL Server reset behind the maintenance application lock, the gate every journal commit holds shared and
/// every maintenance call holds exclusive.
/// <para>
/// <b>There is no race to lose here.</b> The holder takes the lock before the reset is called and keeps it until the
/// call has returned or is proved to be waiting, so the refusal facts see the only outcome the server allows, and the
/// waiting fact asserts nothing until the server itself reports the reset's request as WAITING.
/// </para>
/// <para>
/// The schema application lock comes before all of that. The reset validates the schema behind it, so a host stuck in
/// schema initialization is waited on up to the reset's lock timeout, and no longer than the caller wants. The reset
/// validates on the calling thread, so those facts start it on the thread pool and bound the wait themselves.
/// </para>
/// </summary>
[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalResetContentionTests
{
    private const string SchemaLock = "KhaozEngine.WorldStore.SqlServer.JournalSchema";
    private const string MaintenanceLock = "KhaozEngine.WorldStore.SqlServer.JournalMaintenance";
    private static readonly TimeSpan ShortLockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongLockTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(30);

    [SqlServerFact]
    public Task A_reset_while_a_commit_holds_the_lock_shared_is_refused_and_deletes_nothing()
        => AssertRefusedWhileHeldAsync("Shared");

    [SqlServerFact]
    public Task A_reset_while_maintenance_holds_the_lock_exclusive_is_refused_and_deletes_nothing()
        => AssertRefusedWhileHeldAsync("Exclusive");

    [SqlServerFact]
    public async Task A_reset_waits_behind_the_lock_and_goes_through_once_it_is_released()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();

        await using var holder = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await TakeAsync(holder, held, MaintenanceLock, "Shared") >= 0, "The holder must get the lock before the reset runs.");

        Task<JournalResetResult> reset = SqlServerJournalReset.ResetAsync(
            SqlServerJournalResetHarness.ConnectionString,
            WaiterBound);
        Assert.True(await WaitForLockWaiterAsync(reset), "The reset never showed up waiting on the maintenance lock.");

        // Not merely unfinished. The lock is the reset's first statement, so the journal is untouched too.
        Assert.False(reset.IsCompleted);
        Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());

        await held.RollbackAsync();

        Assert.Equal(before, JournalResetTestSupport.CountsByTable(await reset));
    }

    [SqlServerFact]
    public async Task A_reset_while_schema_initialization_holds_its_lock_is_refused_once_the_lock_timeout_runs_out()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
        string metadata = await SqlServerJournalResetHarness.MetadataAsync();

        await using (var holder = new SqlConnection(SqlServerJournalResetHarness.ConnectionString))
        {
            await holder.OpenAsync();
            await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
            Assert.True(await TakeAsync(holder, held, SchemaLock, "Exclusive") >= 0, "The holder must get the lock before the reset runs.");

            Task<JournalResetResult> reset = Task.Run(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString, ShortLockTimeout));
            try
            {
                Assert.True(
                    await Task.WhenAny(reset, Task.Delay(WaiterBound)) == reset,
                    "The reset outlived its lock timeout behind the schema lock.");
                JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(() => reset);
                Assert.Equal(JournalStoreFailureKind.Timeout, refused.Kind);
                Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
                Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
            }
            finally
            {
                await held.RollbackAsync();
                await Task.WhenAny(reset);
            }
        }

        Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
        Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
    }

    [SqlServerFact]
    public async Task A_reset_waiting_behind_the_schema_lock_stops_when_it_is_cancelled()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
        string metadata = await SqlServerJournalResetHarness.MetadataAsync();

        await using (var holder = new SqlConnection(SqlServerJournalResetHarness.ConnectionString))
        {
            await holder.OpenAsync();
            await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
            Assert.True(await TakeAsync(holder, held, SchemaLock, "Exclusive") >= 0, "The holder must get the lock before the reset runs.");

            using var cancel = new CancellationTokenSource();
            Task<JournalResetResult> reset = Task.Run(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString, LongLockTimeout, cancel.Token));
            try
            {
                Assert.True(await WaitForLockWaiterAsync(reset), "The reset never showed up waiting on the schema lock.");
                await cancel.CancelAsync();

                // Well inside a lock timeout of minutes, so the wait ended because the caller cancelled it.
                Assert.True(
                    await Task.WhenAny(reset, Task.Delay(WaiterBound)) == reset,
                    "The cancelled reset kept waiting behind the schema lock.");
                JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(() => reset);
                Assert.Equal(JournalStoreFailureKind.Cancelled, refused.Kind);
                Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
                Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
            }
            finally
            {
                await held.RollbackAsync();
                await Task.WhenAny(reset);
            }
        }

        Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
        Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
    }

    private static async Task AssertRefusedWhileHeldAsync(string mode)
    {
        await SqlServerJournalResetHarness.SeedAsync();
        KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
        string metadata = await SqlServerJournalResetHarness.MetadataAsync();

        await using (var holder = new SqlConnection(SqlServerJournalResetHarness.ConnectionString))
        {
            await holder.OpenAsync();
            await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
            Assert.True(await TakeAsync(holder, held, MaintenanceLock, mode) >= 0, "The holder must get the lock before the reset runs.");

            JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString, ShortLockTimeout));

            Assert.Equal(JournalStoreFailureKind.Timeout, refused.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
            Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
            await held.RollbackAsync();
        }

        Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
        Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());

        // Once the holder is gone the same call goes through and reports the journal as it stood.
        JournalResetResult reset = await SqlServerJournalReset.ResetAsync(
            SqlServerJournalResetHarness.ConnectionString,
            ShortLockTimeout);
        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
    }

    /// <summary>
    /// Whether a session is WAITING for an exclusive application lock in this database, polled until one is or the
    /// reset has already finished, which would mean it never waited. The collection is serialized, so the reset is
    /// the only session that can be waiting.
    /// </summary>
    private static async Task<bool> WaitForLockWaiterAsync(Task reset)
    {
        var bound = Stopwatch.StartNew();
        await using var connection = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await connection.OpenAsync();
        while (bound.Elapsed < WaiterBound && !reset.IsCompleted)
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_mode = N'X' AND request_status = N'WAIT'
                  AND resource_database_id = DB_ID();
                """;
            if ((int)(await command.ExecuteScalarAsync())! > 0) return true;
            await Task.Delay(20);
        }

        return false;
    }

    /// <summary>
    /// An application lock on a resource name the store names, in the mode a commit, a maintenance call or schema
    /// initialization takes, with no wait at all so a failure to get it is reported here rather than hanging the fact.
    /// </summary>
    private static async Task<int> TakeAsync(SqlConnection connection, SqlTransaction transaction, string resource, string mode)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = @mode,
                @LockOwner = 'Transaction',
                @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = mode;
        return await command.ExecuteScalarAsync() is int code ? code : -999;
    }
}
