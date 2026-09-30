using System;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

internal static class SqlServerSchemaBootstrapProbe
{
    public static async Task<T[]> ConstructWhileHeldAsync<T>(string connectionString, string resource,
        Func<string, T> construct, Func<Task> assertUnchanged)
    {
        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (SqlCommand take = holder.CreateCommand())
        {
            take.Transaction = held;
            take.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction', @LockTimeout = 0;
                SELECT @result;
                """;
            take.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            Assert.True((int)(await take.ExecuteScalarAsync())! >= 0, "The fixture must hold the schema lock first.");
        }

        string application = "schema-bootstrap-" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = application };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<T> first = Task.Run(async () => { await start.Task; return construct(builder.ConnectionString); });
        Task<T> second = Task.Run(async () => { await start.Task; return construct(builder.ConnectionString); });
        start.SetResult();
        try
        {
            Assert.True(await BothAreWaitingAsync(connectionString, application, first, second),
                "Both constructors must wait for the transaction-owned schema lock before DDL.");
            await assertUnchanged();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            await held.RollbackAsync();
            // Observe both constructor tasks before disposing the fixture, even on the RED assertion path.
            await Task.WhenAll(first, second);
        }

        return new[] { await first, await second };
    }

    private static async Task<bool> BothAreWaitingAsync(string connectionString, string application,
        Task first, Task second)
    {
        var bound = Stopwatch.StartNew();
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        while (bound.Elapsed < TimeSpan.FromSeconds(30) && !first.IsCompleted && !second.IsCompleted)
        {
            await using SqlCommand command = observer.CreateCommand();
            command.CommandText = """
                SELECT COUNT(DISTINCT l.request_session_id)
                FROM sys.dm_tran_locks AS l
                JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
                WHERE l.resource_type = N'APPLICATION' AND l.request_mode = N'X'
                  AND l.request_status = N'WAIT' AND l.request_owner_type = N'TRANSACTION'
                  AND l.resource_database_id = DB_ID() AND s.program_name = @application;
                """;
            command.Parameters.Add("@application", SqlDbType.NVarChar, 128).Value = application;
            if ((int)(await command.ExecuteScalarAsync())! == 2) return true;
            await Task.Delay(50);
        }

        return false;
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
