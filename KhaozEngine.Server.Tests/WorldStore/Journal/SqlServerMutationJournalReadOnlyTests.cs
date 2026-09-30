using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerMutationJournalReadOnlyTests : IDisposable
{
    private const string CancellationLock = "KhaozEngine.WorldStore.SqlServer.ReadOnlyCancellationTest";
    private static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(30);
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING");
    private static string DedicatedConnectionString =>
        SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(ConnectionString);
    private static readonly string[] TablesInDropOrder =
    {
        "journal_projection",
        "journal_snapshot",
        "journal_event",
        "journal_operation_stream",
        "journal_operation",
        "journal_stream",
        "journal_metadata",
    };
    private readonly List<SqlServerJournalPrefixStore> ownedStores = new();

    [SqlServerFact]
    public async Task Read_only_store_lists_reads_and_refuses_every_write_without_changing_the_database()
    {
        SqlServerJournalPrefixStore writer = CreatePrefixedStore();
        await SeedAsync(writer);
        string before = await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, writer.Prefix);

        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(DedicatedConnectionString)
        {
            SchemaMode = SqlServerJournalSchemaMode.ReadOnly,
        });
        var reader = new SqlServerJournalPrefixStore(inner, writer.Prefix, writer.TimeProvider);
        JournalStreamPage page = await reader.ListStreamsAsync(new JournalStreamQuery(10));
        JournalSnapshot snapshot = (await reader.LoadSnapshotAsync("player/a"))!;
        JournalEventPage events = await reader.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024));
        JournalProjectionRead projections = await reader.ReadProjectionsAsync(new JournalProjectionQuery("player/a"));
        JournalOperationResolution resolution = await reader.ResolveOperationAsync(Identity(3));
        await AssertRefused(nameof(inner.InitializeAsync), () => reader.InitializeAsync(Initialization(10, "player/c")));
        await AssertRefused(nameof(inner.CommitAsync), () => reader.CommitAsync(Commit(11, "player/a", 2, 7)));
        await AssertRefused(nameof(inner.CompactAsync), () => reader.CompactAsync(new JournalCompaction("player/a", 2, "player.v2", 2, new byte[] { 40 }, 2)));
        await AssertRefused(nameof(inner.PurgeOperationsAsync), () => inner.PurgeOperationsAsync(new JournalOperationPurge(DateTimeOffset.MaxValue, 10)));
        await AssertRefused(nameof(inner.PurgeOperationsByAgeAsync), () => inner.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)));
        await AssertRefused(nameof(inner.RotateStoreEpochAsync), () => inner.RotateStoreEpochAsync());
        string after = await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, writer.Prefix);

        Assert.Equal(new[] { ("player/a", 2L), ("player/b", 0L) }, page.Streams.Select(value => (value.StreamKey, value.HeadVersion)));
        Assert.Null(page.ContinuationKey);
        Assert.Equal(1, snapshot.ThroughVersion);
        Assert.Equal(new byte[] { 30 }, snapshot.Data.ToArray());
        Assert.Equal(new long[] { 1, 2 }, events.Events.Select(value => value.StreamVersion));
        Assert.Equal(new byte[] { 9 }, Assert.Single(projections.Sections).Data.ToArray());
        Assert.Equal(JournalOperationResolutionStatus.Replayed, resolution.Status);
        Assert.Equal(before, after);
    }

    [SqlServerFact]
    public async Task Read_only_open_of_a_database_with_no_journal_schema_is_refused()
    {
        _ = new SqlServerMutationJournalStore(DedicatedConnectionString);
        string before = await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, "journal-test/none/");
        var hook = new SqlServerJournalSchemaTestHook(async (connection, transaction, cancellationToken) =>
        {
            foreach (string table in TablesInDropOrder)
            {
                await using SqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DROP TABLE dbo.{table};";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        });

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => OpenReadOnly(hook));

        AssertSchemaRefusal(exception);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, "journal-test/none/"));
    }

    [SqlServerFact]
    public async Task Read_only_open_of_an_older_schema_is_refused_and_not_migrated()
    {
        _ = new SqlServerMutationJournalStore(DedicatedConnectionString);
        string before = await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, "journal-test/none/");
        short isolationLevel = 0;
        string applicationLock = string.Empty;
        var hook = new SqlServerJournalSchemaTestHook(async (connection, transaction, cancellationToken) =>
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dbo.journal_metadata SET schema_version = 1 WHERE metadata_key = 1;
                SELECT transaction_isolation_level,
                       APPLOCK_MODE('public', 'KhaozEngine.WorldStore.SqlServer.JournalSchema', 'Transaction')
                FROM sys.dm_exec_sessions WHERE session_id = @@SPID;
                """;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            isolationLevel = reader.GetInt16(0);
            applicationLock = reader.GetString(1);
        });

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => OpenReadOnly(hook));

        AssertSchemaRefusal(exception);
        Assert.Contains("unsupported version '1'", exception.Message, StringComparison.Ordinal);
        Assert.Equal((short)2, isolationLevel);
        Assert.Equal("NoLock", applicationLock);
        Assert.Equal(before, await SqlServerJournalFingerprint.CaptureAsync(DedicatedConnectionString, "journal-test/none/"));
    }

    [SqlServerFact]
    public async Task Read_only_schema_command_cancelled_by_the_caller_is_cancelled()
    {
        _ = new SqlServerMutationJournalStore(DedicatedConnectionString);
        await using var holder = new SqlConnection(DedicatedConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await TakeCancellationLockAsync(holder, held) >= 0, "The holder must get the lock before validation runs.");

        using var cancellation = new CancellationTokenSource();
        var hook = new SqlServerJournalSchemaTestHook(async (connection, transaction, cancellationToken) =>
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 300;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 300000;
                SELECT @result;
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = CancellationLock;
            await command.ExecuteScalarAsync(cancellationToken);
        });
        Task<SqlServerMutationJournalStore> opening = Task.Run(() => new SqlServerMutationJournalStore(
            new SqlServerMutationJournalStoreOptions(DedicatedConnectionString)
            {
                SchemaMode = SqlServerJournalSchemaMode.ReadOnly,
            },
            testHook: null,
            schemaTestHook: hook,
            schemaCancellation: cancellation.Token));

        try
        {
            Assert.True(await WaitForCancellationLockAsync(opening), "Validation never showed up waiting on its command.");
            await cancellation.CancelAsync();
            Assert.True(
                await Task.WhenAny(opening, Task.Delay(WaiterBound)) == opening,
                "The cancelled read only validation kept waiting on its command.");
            JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(() => opening);
            Assert.Equal(JournalStoreFailureKind.Cancelled, exception.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, exception.Certainty);
            Assert.Equal(JournalStoreFailureScope.WholeStore, exception.Scope);
        }
        finally
        {
            await held.RollbackAsync();
            await Task.WhenAny(opening, Task.Delay(WaiterBound));
        }
    }

    private static SqlServerMutationJournalStore OpenReadOnly(SqlServerJournalSchemaTestHook hook)
        => new(
            new SqlServerMutationJournalStoreOptions(DedicatedConnectionString) { SchemaMode = SqlServerJournalSchemaMode.ReadOnly },
            testHook: null,
            schemaTestHook: hook);

    private static async Task<int> TakeCancellationLockAsync(SqlConnection connection, SqlTransaction transaction)
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
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = CancellationLock;
        return await command.ExecuteScalarAsync() is int code ? code : -999;
    }

    private static async Task<bool> WaitForCancellationLockAsync(Task opening)
    {
        var bound = Stopwatch.StartNew();
        await using var connection = new SqlConnection(DedicatedConnectionString);
        await connection.OpenAsync();
        while (bound.Elapsed < WaiterBound && !opening.IsCompleted)
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

    private SqlServerJournalPrefixStore CreatePrefixedStore()
    {
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(DedicatedConnectionString)
        {
            TimeProvider = clock,
        });
        var store = new SqlServerJournalPrefixStore(inner, $"journal-test/{Guid.NewGuid():N}/", clock);
        ownedStores.Add(store);
        return store;
    }

    private static async Task SeedAsync(SqlServerJournalPrefixStore writer)
    {
        await writer.InitializeAsync(Initialization(1, "player/a", new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 5 })));
        await writer.InitializeAsync(Initialization(2, "player/b"));
        await writer.CommitAsync(new JournalCommit(
            Identity(3),
            new[] { new JournalStreamMutation("player/a", 0, new[] { Event(1), Event(2) }) },
            new[] { new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 9 }) },
            "result.v1",
            1,
            new byte[] { 3 }));
        await writer.CompactAsync(new JournalCompaction("player/a", 1, "player.v1", 1, new byte[] { 30 }, null));
    }

    private static async Task AssertRefused(string operation, Func<Task> write)
    {
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(write);
        Assert.Contains(nameof(SqlServerJournalSchemaMode.ReadOnly), exception.Message, StringComparison.Ordinal);
        Assert.Contains(operation, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertSchemaRefusal(JournalStoreException exception)
    {
        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, exception.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, exception.Scope);
        Assert.Contains(SqlServerJournalSchema.RequiredMigration, exception.Message, StringComparison.Ordinal);
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new[] { checked((byte)suffix) });

    private static JournalInitialization Initialization(int suffix, string streamKey, params JournalProjectionWrite[] projections)
        => new(Identity(suffix), streamKey, "player.v1", 1, Array.Empty<byte>(), projections, "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, string streamKey, long expectedVersion, byte eventValue)
        => new(
            Identity(suffix),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, new[] { Event(eventValue) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { eventValue });

    private static JournalEvent Event(byte value) => new("state.changed", 1, new[] { value });

    public void Dispose()
    {
        foreach (SqlServerJournalPrefixStore store in ownedStores)
            SqlServerJournalTestDatabase.CleanupAsync(DedicatedConnectionString, store.Prefix, store.OwnedOperationIds).GetAwaiter().GetResult();
    }
}

/// <summary>The journal schema objects, the metadata row, and every row under one stream prefix, as JSON. Two equal
/// captures mean nothing a read only store could reach was created, altered, migrated, or written.</summary>
internal static class SqlServerJournalFingerprint
{
    internal static async Task<string> CaptureAsync(string connectionString, string prefix)
    {
        string like = SqlServerJournalTestDatabase.EscapeLike(prefix) + "%";
        string[] queries =
        {
            """
            SELECT o.name, o.type, o.modify_date, OBJECT_NAME(o.parent_object_id) AS parent
            FROM sys.objects o WHERE o.schema_id = SCHEMA_ID(N'dbo') AND o.name LIKE N'%journal[_]%'
            ORDER BY o.name FOR JSON PATH;
            """,
            """
            SELECT t.name AS table_name, i.name, i.type_desc FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'journal[_]%'
            ORDER BY t.name, i.name FOR JSON PATH;
            """,
            "SELECT * FROM dbo.journal_metadata ORDER BY metadata_key FOR JSON PATH;",
            "SELECT * FROM dbo.journal_stream WHERE stream_key LIKE @prefix ESCAPE '\\' ORDER BY stream_key FOR JSON PATH;",
            "SELECT * FROM dbo.journal_event WHERE stream_key LIKE @prefix ESCAPE '\\' ORDER BY stream_key, stream_version FOR JSON PATH;",
            "SELECT * FROM dbo.journal_snapshot WHERE stream_key LIKE @prefix ESCAPE '\\' ORDER BY stream_key FOR JSON PATH;",
            "SELECT * FROM dbo.journal_projection WHERE stream_key LIKE @prefix ESCAPE '\\' ORDER BY stream_key, section_name FOR JSON PATH;",
            "SELECT * FROM dbo.journal_operation_stream WHERE stream_key LIKE @prefix ESCAPE '\\' ORDER BY operation_id, stream_key FOR JSON PATH;",
            """
            SELECT o.* FROM dbo.journal_operation o
            WHERE o.operation_id IN (SELECT s.operation_id FROM dbo.journal_operation_stream s WHERE s.stream_key LIKE @prefix ESCAPE '\')
            ORDER BY o.operation_id FOR JSON PATH;
            """,
        };
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var capture = new StringBuilder();
        foreach (string query in queries)
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = query;
            command.Parameters.Add("@prefix", SqlDbType.NVarChar, 256).Value = like;
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) capture.Append(reader.GetString(0));
            capture.Append('\n');
        }
        return capture.ToString();
    }
}
