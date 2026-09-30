using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// Schema version 3 of the SQL Server journal, the SQLite facts' twin: every row records when it was created, a fresh
/// store writes the time from its own clock, and the version 2 to 3 migration fills a legacy row only from a time the
/// database proves.
/// <para>
/// Each fact starts from a fresh journal: it drops every journal table and builds the version it starts from out of
/// that version's embedded script. That wipes the WHOLE journal, so like the reset facts these run only against a
/// database named for journal tests and only inside the serialized <c>SQL Server mutation journal</c> collection.
/// Each leaves a fresh version 3 journal behind for the facts after it.
/// </para>
/// </summary>
[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalRowTimestampTests : IDisposable
{
    private const string MigrationName = "sqlserver-journal-v3-row-timestamps";

    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(20);

    /// <summary>The four tables version 3 gives a <c>created_at_utc</c> column.</summary>
    private static readonly string[] CreationColumnTables =
    {
        "journal_metadata", "journal_stream", "journal_operation_stream", "journal_projection",
    };

    /// <summary>Every journal table and the column that holds its creation time.</summary>
    private static readonly (string Table, string Column)[] CreationTimes =
    {
        ("journal_event", "committed_at_utc"),
        ("journal_metadata", "created_at_utc"),
        ("journal_operation", "committed_at_utc"),
        ("journal_operation_stream", "created_at_utc"),
        ("journal_projection", "created_at_utc"),
        ("journal_snapshot", "created_at_utc"),
        ("journal_stream", "created_at_utc"),
    };

    /// <summary>Children before parents, so no drop finds a key still referencing its table.</summary>
    private static readonly string[] TablesInDropOrder =
    {
        "journal_projection", "journal_snapshot", "journal_event", "journal_operation_stream",
        "journal_operation", "journal_stream", "journal_metadata",
    };

    private static readonly Guid LegacyInitialization = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid LegacyCommit = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid LegacyEmptyCommit = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private bool rebuilt;

    private static string ConnectionString
        => SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(
            Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING"));

    [SqlServerFact]
    public async Task Fresh_store_creates_version_3_with_nullable_creation_columns()
    {
        await RebuildJournalAsync(null);
        _ = Open(new SqlServerJournalManualTimeProvider(T0));

        Assert.Equal(3, await SchemaVersionAsync());
        foreach (string table in CreationColumnTables)
            Assert.Equal("datetimeoffset(7) NULL", await ScalarAsync("""
                SELECT CONCAT(ty.name, N'(', c.scale, N') ', CASE c.is_nullable WHEN 1 THEN N'NULL' ELSE N'NOT NULL' END)
                FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(@table) AND c.name = N'created_at_utc';
                """, ("@table", "dbo." + table)));
    }

    [SqlServerFact]
    public async Task Every_row_written_carries_its_creation_time()
    {
        await RebuildJournalAsync(null);
        var clock = new SqlServerJournalManualTimeProvider(T0);
        SqlServerMutationJournalStore store = Open(clock);
        DateTimeOffset? metadataCreated = await TimeAsync("SELECT created_at_utc FROM dbo.journal_metadata WHERE metadata_key = 1;");
        DateTimeOffset? metadataUpdated = await TimeAsync("SELECT updated_at_utc FROM dbo.journal_metadata WHERE metadata_key = 1;");

        await store.InitializeAsync(Initialization(1, "player/a"));
        await store.InitializeAsync(Initialization(2, "player/b", Projection("player/b", "profile", 1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset t1 = clock.GetUtcNow();
        await store.CommitAsync(Commit(3, "player/a", 0, Projection("player/a", "bag", 2)));
        clock.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset t2 = clock.GetUtcNow();
        await store.CommitAsync(Commit(4, "player/a", 1, Projection("player/a", "bag", 3)));
        await store.CompactAsync(new JournalCompaction("player/a", 1, "player.v1", 1, new byte[] { 30 }, 1));
        await store.RotateStoreEpochAsync();

        Assert.NotNull(metadataCreated);
        Assert.Equal(metadataUpdated, metadataCreated);
        Assert.Equal(metadataCreated, await TimeAsync("SELECT created_at_utc FROM dbo.journal_metadata WHERE metadata_key = 1;"));
        Assert.Equal(t2, await TimeAsync("SELECT updated_at_utc FROM dbo.journal_metadata WHERE metadata_key = 1;"));
        Assert.Equal((T0, t2), await StreamTimesAsync("player/a"));
        Assert.Equal((T0, T0), await StreamTimesAsync("player/b"));
        Assert.Equal((t1, t2), await ProjectionTimesAsync("player/a", "bag"));
        Assert.Equal((T0, T0), await ProjectionTimesAsync("player/b", "profile"));
        Assert.Equal(4, await CountAsync("SELECT COUNT(*) FROM dbo.journal_operation_stream;"));
        Assert.Equal(4, await CountAsync("""
            SELECT COUNT(*) FROM dbo.journal_operation_stream s
            JOIN dbo.journal_operation o ON o.operation_id = s.operation_id
            WHERE s.created_at_utc = o.committed_at_utc;
            """));
        Assert.Equal(
            CreationTimes.Select(value => value.Table).OrderBy(value => value, StringComparer.Ordinal),
            await JournalTablesAsync());
        foreach ((string table, string column) in CreationTimes)
        {
            Assert.True(await CountAsync($"SELECT COUNT(*) FROM dbo.{table};") > 0, $"{table} has no row to prove");
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM dbo.{table} WHERE {column} IS NULL;"));
        }
    }

    [SqlServerFact]
    public async Task Version_2_database_migrates_and_backfills_exactly()
    {
        await RebuildJournalAsync(SqlServerMutationJournalStore.VersionTwoSchemaSqlForTest);
        await SeedLegacyRowsAsync();
        Assert.Equal(2, await SchemaVersionAsync());
        Assert.Equal(0, await CreationColumnCountAsync());

        var clock = new SqlServerJournalManualTimeProvider(T0.AddHours(1));
        SqlServerMutationJournalStore store = Open(clock);

        Assert.Equal(3, await SchemaVersionAsync());
        await AssertLegacyBackfillAsync();
        _ = Open(clock, SqlServerJournalSchemaMode.ValidateOnly);

        await store.InitializeAsync(Initialization(1, "player/new"));
        Assert.Equal((T0.AddHours(1), T0.AddHours(1)), await StreamTimesAsync("player/new"));
    }

    [SqlServerFact]
    public async Task Version_1_database_chains_to_version_3()
    {
        await RebuildJournalAsync(SqlServerMutationJournalStore.VersionOneSchemaSqlForTest);
        await SeedLegacyRowsAsync();
        Assert.Equal(1, await SchemaVersionAsync());

        _ = Open(new SqlServerJournalManualTimeProvider(T0.AddHours(1)));

        Assert.Equal(3, await SchemaVersionAsync());
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.journal_operation') AND name = N'retention_started_at_utc';"));
        Assert.Equal(CreationColumnTables.Length, await CreationColumnCountAsync());
        await AssertLegacyBackfillAsync();
    }

    [SqlServerFact]
    public Task Half_migrated_database_with_one_column_added_finishes_on_reopen() => HalfMigratedFinishesOnReopenAsync(1);

    [SqlServerFact]
    public Task Half_migrated_database_with_every_column_added_finishes_on_reopen() => HalfMigratedFinishesOnReopenAsync(4);

    [SqlServerFact]
    public Task ValidateOnly_refuses_version_2_naming_the_migration() => RefusesVersionTwoAsync(SqlServerJournalSchemaMode.ValidateOnly);

    [SqlServerFact]
    public Task ReadOnly_refuses_version_2_naming_the_migration() => RefusesVersionTwoAsync(SqlServerJournalSchemaMode.ReadOnly);

    [SqlServerFact]
    public async Task Two_opens_race_one_migration()
    {
        await RebuildJournalAsync(SqlServerMutationJournalStore.VersionTwoSchemaSqlForTest);
        await SeedLegacyRowsAsync();
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.journal_stream WHERE stream_key = N'legacy/a';"));
        using var probe = new SqlServerJournalMigrationBackfillProbe(ConnectionString);
        var clock = new SqlServerJournalManualTimeProvider(T0.AddHours(1));
        var firstHoldsSchemaLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondWaited = false;
        var hook = new SqlServerJournalSchemaTestHook(async (_, _, _) =>
        {
            firstHoldsSchemaLock.TrySetResult();
            secondWaited = await WaitForSchemaLockWaiterAsync();
        });

        Task<SqlServerMutationJournalStore> first = Task.Run(() => new SqlServerMutationJournalStore(
            Options(clock, SqlServerJournalSchemaMode.AutoCreate),
            testHook: null,
            schemaTestHook: hook));
        if (await Task.WhenAny(firstHoldsSchemaLock.Task, first) == first) await first;
        Task<SqlServerMutationJournalStore> second = Task.Run(() => Open(clock));
        await Task.WhenAll(first, second);

        Assert.True(secondWaited, "The second open never waited on the schema lock the first one held.");
        Assert.Equal(3, await SchemaVersionAsync());
        await AssertLegacyBackfillAsync();
        Assert.Equal(1, probe.Updates);
    }

    private async Task HalfMigratedFinishesOnReopenAsync(int columnsAdded)
    {
        await RebuildJournalAsync(SqlServerMutationJournalStore.VersionTwoSchemaSqlForTest);
        await SeedLegacyRowsAsync();
        foreach (string add in SqlServerMutationJournalStore.VersionTwoMigrationSqlForTest.Take(columnsAdded))
            await ExecuteAsync(add);
        Assert.Equal(2, await SchemaVersionAsync());
        Assert.Equal(columnsAdded, await CreationColumnCountAsync());

        _ = Open(new SqlServerJournalManualTimeProvider(T0.AddHours(1)));

        Assert.Equal(3, await SchemaVersionAsync());
        Assert.Equal(CreationColumnTables.Length, await CreationColumnCountAsync());
        await AssertLegacyBackfillAsync();
    }

    private async Task RefusesVersionTwoAsync(SqlServerJournalSchemaMode mode)
    {
        await RebuildJournalAsync(SqlServerMutationJournalStore.VersionTwoSchemaSqlForTest);
        await SeedLegacyRowsAsync();
        string before = await SqlServerJournalFingerprint.CaptureAsync(ConnectionString, "legacy/");

        JournalStoreException refused = Assert.Throws<JournalStoreException>(
            () => Open(new SqlServerJournalManualTimeProvider(T0), mode));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Contains("unsupported version '2'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SqlServerJournalFingerprint.CaptureAsync(ConnectionString, "legacy/"));
        Assert.Equal(2, await SchemaVersionAsync());
        Assert.Equal(0, await CreationColumnCountAsync());
    }

    /// <summary>
    /// The legacy rows <see cref="SeedLegacyRowsAsync"/> wrote, after a migration. Stream A kept its initialization
    /// snapshot, so its creation time is that snapshot's. Stream B's snapshot was replaced, so its creation time is
    /// unknown even though a retained operation looks like an initialization. Receipt stream ranges take their
    /// operation's commit time, and nothing else is guessed.
    /// </summary>
    private static async Task AssertLegacyBackfillAsync()
    {
        Assert.Equal((T0, T0.AddMinutes(3)), await StreamTimesAsync("legacy/a"));
        Assert.Equal(((DateTimeOffset?)null, T0.AddMinutes(20)), await StreamTimesAsync("legacy/b"));
        Assert.Equal(T0, await OperationStreamCreatedAsync(LegacyInitialization));
        Assert.Equal(T0.AddMinutes(3), await OperationStreamCreatedAsync(LegacyCommit));
        Assert.Equal(T0.AddMinutes(10), await OperationStreamCreatedAsync(LegacyEmptyCommit));
        Assert.Equal(((DateTimeOffset?)null, T0.AddMinutes(3)), await ProjectionTimesAsync("legacy/a", "bag"));
        Assert.Null(await TimeAsync("SELECT created_at_utc FROM dbo.journal_metadata WHERE metadata_key = 1;"));
    }

    /// <summary>
    /// Rows written in the version 1 column set, which a version 2 table also accepts. Stream A still holds its
    /// initialization snapshot at version 0. Stream B was compacted to version 5, and its only retained operation
    /// has the empty shape an initialization has, at a later time.
    /// </summary>
    private static Task SeedLegacyRowsAsync()
        => ExecuteAsync(
            """
            INSERT INTO dbo.journal_stream(stream_key, current_version, retained_floor, updated_at_utc)
            VALUES (N'legacy/a', 1, 0, @t3), (N'legacy/b', 5, 5, @t20);
            INSERT INTO dbo.journal_snapshot(
                stream_key, through_version, snapshot_schema, snapshot_schema_version, data, data_sha256, created_at_utc)
            VALUES (N'legacy/a', 0, N'player.v1', 1, 0x, @hash, @t0),
                   (N'legacy/b', 5, N'player.v1', 1, 0x05, @hash, @t20);
            INSERT INTO dbo.journal_event(
                stream_key, stream_version, operation_id, operation_ordinal, event_type,
                event_schema_version, payload, payload_sha256, committed_at_utc)
            VALUES (N'legacy/a', 1, @commit, 0, N'state.changed', 1, 0x01, @hash, @t3);
            INSERT INTO dbo.journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES (@initialization, N'bank.deposit', 1, @hash, 1, @hash, N'result.v1', 1, 0x, @hash, @t0),
                   (@commit, N'bank.deposit', 1, @hash, 1, @hash, N'result.v1', 1, 0x, @hash, @t3),
                   (@empty, N'bank.deposit', 1, @hash, 1, @hash, N'result.v1', 1, 0x, @hash, @t10);
            INSERT INTO dbo.journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count)
            VALUES (@initialization, N'legacy/a', 0, 0, 0),
                   (@commit, N'legacy/a', 0, 1, 1),
                   (@empty, N'legacy/b', 0, 0, 0);
            INSERT INTO dbo.journal_projection(
                stream_key, section_name, source_version, projection_schema,
                projection_schema_version, data, data_sha256, updated_at_utc)
            VALUES (N'legacy/a', N'bag', 1, N'section.v1', 1, 0x09, @hash, @t3);
            """,
            ("@t0", T0),
            ("@t3", T0.AddMinutes(3)),
            ("@t10", T0.AddMinutes(10)),
            ("@t20", T0.AddMinutes(20)),
            ("@hash", new byte[32]),
            ("@initialization", LegacyInitialization),
            ("@commit", LegacyCommit),
            ("@empty", LegacyEmptyCommit));

    /// <summary>Drops every journal table, then runs <paramref name="schemaSql"/> when one is given.</summary>
    private async Task RebuildJournalAsync(string? schemaSql)
    {
        rebuilt = true;
        await ExecuteAsync(string.Concat(TablesInDropOrder.Select(table => $"DROP TABLE IF EXISTS dbo.{table};\n")));
        if (schemaSql is not null) await ExecuteAsync(schemaSql);
    }

    /// <summary>Polls until another session waits for an exclusive application lock in this database.</summary>
    private static async Task<bool> WaitForSchemaLockWaiterAsync()
    {
        var bound = Stopwatch.StartNew();
        while (bound.Elapsed < WaiterBound)
        {
            if (await CountAsync("""
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_mode = N'X' AND request_status = N'WAIT'
                  AND resource_database_id = DB_ID();
                """) > 0)
                return true;
            await Task.Delay(20);
        }

        return false;
    }

    private static SqlServerMutationJournalStoreOptions Options(TimeProvider clock, SqlServerJournalSchemaMode mode)
        => new(ConnectionString) { SchemaMode = mode, TimeProvider = clock };

    private static SqlServerMutationJournalStore Open(
        TimeProvider clock,
        SqlServerJournalSchemaMode mode = SqlServerJournalSchemaMode.AutoCreate)
        => new(Options(clock, mode));

    private static async Task<int> SchemaVersionAsync()
        => (int)(await ScalarAsync("SELECT schema_version FROM dbo.journal_metadata WHERE metadata_key = 1;"))!;

    private static Task<long> CreationColumnCountAsync()
        => CountAsync($"""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id IN ({string.Join(", ", CreationColumnTables.Select(table => $"OBJECT_ID(N'dbo.{table}')"))})
              AND name = N'created_at_utc';
            """);

    private static Task<(DateTimeOffset? Created, DateTimeOffset? Updated)> StreamTimesAsync(string streamKey)
        => TimesAsync(
            "SELECT created_at_utc, updated_at_utc FROM dbo.journal_stream WHERE stream_key = @stream;",
            ("@stream", streamKey));

    private static Task<(DateTimeOffset? Created, DateTimeOffset? Updated)> ProjectionTimesAsync(string streamKey, string section)
        => TimesAsync(
            "SELECT created_at_utc, updated_at_utc FROM dbo.journal_projection WHERE stream_key = @stream AND section_name = @section;",
            ("@stream", streamKey),
            ("@section", section));

    private static Task<DateTimeOffset?> OperationStreamCreatedAsync(Guid operationId)
        => TimeAsync("SELECT created_at_utc FROM dbo.journal_operation_stream WHERE operation_id = @id;", ("@id", operationId));

    private static async Task<IReadOnlyList<string>> JournalTablesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'journal[_]%';";
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The one row <paramref name="sql"/> selects, as its creation and update times.</summary>
    private static async Task<(DateTimeOffset? Created, DateTimeOffset? Updated)> TimesAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = Command(connection, sql, parameters);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The row is missing.");
        (DateTimeOffset? Created, DateTimeOffset? Updated) times = (
            reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1));
        Assert.False(await reader.ReadAsync(), "More than one row matched.");
        return times;
    }

    private static async Task<DateTimeOffset?> TimeAsync(string sql, params (string Name, object Value)[] parameters)
    {
        object? value = await ScalarAsync(sql, parameters);
        Assert.NotNull(value);
        return value is DBNull ? null : (DateTimeOffset)value;
    }

    private static async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql));

    private static async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = Command(connection, sql, parameters);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlCommand Command(SqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            SqlParameter parameter = value switch
            {
                string => command.Parameters.Add(name, SqlDbType.NVarChar, 256),
                DateTimeOffset => command.Parameters.Add(name, SqlDbType.DateTimeOffset),
                Guid => command.Parameters.Add(name, SqlDbType.UniqueIdentifier),
                byte[] bytes => command.Parameters.Add(name, SqlDbType.Binary, bytes.Length),
                _ => throw new ArgumentException($"Unsupported parameter type '{value.GetType().Name}'.", nameof(parameters)),
            };
            parameter.Value = value;
        }
        return command;
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new[] { checked((byte)suffix) });

    private static JournalInitialization Initialization(int suffix, string streamKey, params JournalProjectionWrite[] projections)
        => new(Identity(suffix), streamKey, "player.v1", 1, Array.Empty<byte>(), projections, "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, string streamKey, long expectedVersion, JournalProjectionWrite projection)
        => new(
            Identity(suffix),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, new[] { new JournalEvent("state.changed", 1, new[] { checked((byte)suffix) }) }) },
            new[] { projection },
            "result.v1",
            1,
            new byte[] { 1 });

    private static JournalProjectionWrite Projection(string streamKey, string section, byte value)
        => new(streamKey, section, "section.v1", 1, new[] { value });

    /// <summary>Leaves a fresh, empty version 3 journal for the facts that follow.</summary>
    public void Dispose()
    {
        if (!rebuilt) return;
        RebuildJournalAsync(null).GetAwaiter().GetResult();
        _ = new SqlServerMutationJournalStore(ConnectionString);
    }
}
