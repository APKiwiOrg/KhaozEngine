using System;
using System.Data;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class MutationJournalDatabaseClockPurgeTests
{
    private static readonly TimeSpan RetryHorizon = TimeSpan.FromHours(1);

    [Fact]
    public async Task In_memory_age_purge_includes_the_exact_retry_horizon_boundary()
    {
        var clock = new Task6ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, RetryHorizon, clock);
        await store.InitializeAsync(Initialization(1));
        clock.Advance(RetryHorizon);

        JournalOperationPurgeResult result = await store.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal((1, 1, 0), (result.ScannedCount, result.DeletedCount, result.IneligibleCount));
        Assert.Equal(clock.GetUtcNow(), result.EvaluatedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.EffectiveCutoffUtc);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
    }

    [Fact]
    public async Task In_memory_age_purge_deletes_oldest_first_with_a_hard_batch_bound()
    {
        var clock = new Task6ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, TimeSpan.Zero, clock);
        await store.InitializeAsync(Initialization(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.CommitAsync(Commit(3, 1, new byte[] { 3 }, 3));

        JournalOperationPurgeResult result = await store.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 2));

        Assert.Equal((2, 2), (result.ScannedCount, result.DeletedCount));
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(3))).Status);
    }

    [Fact]
    public async Task In_memory_age_purge_serializes_with_a_concurrent_young_commit()
    {
        var clock = new Task6ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, RetryHorizon, clock);
        await store.InitializeAsync(Initialization(1));
        clock.Advance(TimeSpan.FromHours(2));

        await Task.WhenAll(
            store.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)),
            store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2)));

        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [Theory]
    [InlineData(-365)]
    [InlineData(365)]
    public async Task Sqlite_new_receipt_retention_time_is_database_owned(int processClockDays)
    {
        using var scope = new Task6SqliteScope();
        var processClock = new Task6ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(processClockDays));
        using SqliteMutationJournalStore store = scope.Database.Open(
            scope.Path,
            new SqliteMutationJournalStoreOptions(scope.Database.ConnectionString(scope.Path))
            {
                MinimumRetryHorizon = RetryHorizon,
                TimeProvider = processClock,
            });

        JournalInitializeResult initialized = await store.InitializeAsync(Initialization(1));

        long retentionStarted = scope.Database.ScalarLong(scope.Path, "SELECT retention_started_at_utc FROM journal_operation;");
        Assert.InRange(retentionStarted, DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
        Assert.Equal(processClock.GetUtcNow().ToUnixTimeMilliseconds(), initialized.Receipt!.CommittedAtUtc.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(-365)]
    [InlineData(365)]
    public async Task Sqlite_age_purge_uses_database_time_under_process_clock_skew(int processClockDays)
    {
        using var scope = new Task6SqliteScope();
        var processClock = new Task6ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(processClockDays));
        using SqliteMutationJournalStore store = scope.Database.Open(
            scope.Path,
            new SqliteMutationJournalStoreOptions(scope.Database.ConnectionString(scope.Path))
            {
                MinimumRetryHorizon = RetryHorizon,
                TimeProvider = processClock,
            });
        await store.InitializeAsync(Initialization(1));
        await store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2));
        await scope.Database.ExecuteAsync(
            scope.Path,
            """
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now', $age) - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = $id;
            """,
            ("$age", (object)"-2 hours"),
            ("$id", MutationJournalTask6TestSupport.Identity(1).OperationId.ToString("D")));
        await scope.Database.ExecuteAsync(
            scope.Path,
            """
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now', $age) - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = $id;
            """,
            ("$age", (object)"-30 minutes"),
            ("$id", MutationJournalTask6TestSupport.Identity(2).OperationId.ToString("D")));

        JournalOperationPurgeResult result = await store.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal((1, 1, 0), (result.ScannedCount, result.DeletedCount, result.IneligibleCount));
        Assert.InRange(result.EvaluatedAtUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(result.EvaluatedAtUtc - RetryHorizon, result.EffectiveCutoffUtc);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [Fact]
    public async Task Sqlite_age_purge_serializes_with_a_concurrent_young_commit()
    {
        using var scope = new Task6SqliteScope();
        using SqliteMutationJournalStore first = scope.Open(retryHorizon: RetryHorizon);
        using SqliteMutationJournalStore second = scope.Database.Open(
            scope.Path,
            new SqliteMutationJournalStoreOptions(scope.Database.ConnectionString(scope.Path))
            {
                MinimumRetryHorizon = RetryHorizon,
            });
        await first.InitializeAsync(Initialization(1));
        await scope.Database.ExecuteAsync(
            scope.Path,
            """
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now', '-2 hours') - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = $id;
            """,
            ("$id", (object)MutationJournalTask6TestSupport.Identity(1).OperationId.ToString("D")));

        await Task.WhenAll(
            first.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)),
            second.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2)));

        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await first.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await first.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [Fact]
    public async Task Sqlite_cutoff_purge_clips_against_database_time_under_process_clock_skew()
    {
        using var scope = new Task6SqliteScope();
        var processClock = new Task6ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(365));
        using SqliteMutationJournalStore store = scope.Database.Open(
            scope.Path,
            new SqliteMutationJournalStoreOptions(scope.Database.ConnectionString(scope.Path))
            {
                MinimumRetryHorizon = RetryHorizon,
                TimeProvider = processClock,
            });
        await store.InitializeAsync(Initialization(1));
        await store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2));
        await SetSqliteCommittedAgeAsync(scope, 1, "-2 hours");
        await SetSqliteCommittedAgeAsync(scope, 2, "-30 minutes");
        await SetSqliteRetentionAgeAsync(scope, 1, "-2 hours");
        await SetSqliteRetentionAgeAsync(scope, 2, "-30 minutes");

        JournalOperationPurgeResult result = await store.PurgeOperationsAsync(
            new JournalOperationPurge(DateTimeOffset.MaxValue, 10));

        Assert.Equal((1, 1, 0), (result.ScannedCount, result.DeletedCount, result.IneligibleCount));
        Assert.InRange(result.EvaluatedAtUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(result.EvaluatedAtUtc - RetryHorizon, result.EffectiveCutoffUtc);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [Fact]
    public async Task Sqlite_unguarded_legacy_delete_rolls_back_children_and_parent()
    {
        using var scope = new Task6SqliteScope();
        using SqliteMutationJournalStore store = scope.Open(retryHorizon: TimeSpan.Zero);
        await store.InitializeAsync(Initialization(1));

        Assert.Throws<SqliteException>(() => scope.Database.Execute(
            scope.Path,
            """
            BEGIN IMMEDIATE;
            DELETE FROM journal_operation_stream WHERE operation_id = $id;
            DELETE FROM journal_operation WHERE operation_id = $id;
            COMMIT;
            """,
            ("$id", (object)MutationJournalTask6TestSupport.Identity(1).OperationId.ToString("D"))));

        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
    }

    [Fact]
    public async Task Sqlite_purge_guard_resets_after_a_transaction_failure()
    {
        using var scope = new Task6SqliteScope();
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.BeforeCommit) throw new Task6InjectedFailure();
        });
        using SqliteMutationJournalStore store = scope.Open(hook, TimeSpan.Zero);
        await store.InitializeAsync(Initialization(1));
        armed = true;

        await Assert.ThrowsAsync<Task6InjectedFailure>(() =>
            store.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)));
        armed = false;
        JournalOperationPurgeResult retry = await store.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal(1, retry.DeletedCount);
    }

    [SqlServerFact]
    public async Task Sql_server_age_purge_uses_database_time_under_process_clock_skew()
    {
        await AssertSqlServerDatabaseClockPurgeAsync(-365);
        await AssertSqlServerDatabaseClockPurgeAsync(365);
    }

    [SqlServerFact]
    public async Task Sql_server_cutoff_purge_clips_against_database_time_under_process_clock_skew()
    {
        using var scope = new Task6SqlServerScope();
        var processClock = new Task6ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(365));
        SqlServerJournalPrefixStore store = scope.Open(retryHorizon: RetryHorizon, clock: processClock);
        await store.InitializeAsync(Initialization(1));
        await store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2));
        await SetSqlServerCommittedAgeAsync(
            scope.ConnectionString,
            store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId),
            -120);
        await SetSqlServerCommittedAgeAsync(
            scope.ConnectionString,
            store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(2).OperationId),
            -30);
        await SetSqlServerOperationAgeAsync(
            scope.ConnectionString,
            store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId),
            -120);
        await SetSqlServerOperationAgeAsync(
            scope.ConnectionString,
            store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(2).OperationId),
            -30);

        JournalOperationPurgeResult result = await store.Maintenance.PurgeOperationsAsync(
            new JournalOperationPurge(DateTimeOffset.MaxValue, 10));

        Assert.Equal((1, 1, 0), (result.ScannedCount, result.DeletedCount, result.IneligibleCount));
        Assert.InRange(result.EvaluatedAtUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(result.EvaluatedAtUtc - RetryHorizon, result.EffectiveCutoffUtc);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [SqlServerFact]
    public async Task Sql_server_unguarded_legacy_delete_rolls_back_children_and_parent()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore store = scope.Open(retryHorizon: TimeSpan.Zero);
        await store.InitializeAsync(Initialization(1));
        Guid operationId = store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId);

        await AssertSqlServerLegacyDeleteRejectedAsync(scope.ConnectionString, operationId);

        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
    }

    [SqlServerFact]
    public async Task Sql_server_purge_guard_resets_after_a_transaction_failure()
    {
        using var scope = new Task6SqlServerScope();
        bool armed = false;
        var hook = new SqlServerJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.BeforeCommit) throw new Task6InjectedFailure();
        });
        SqlServerJournalPrefixStore store = scope.Open(hook, TimeSpan.Zero);
        await store.InitializeAsync(Initialization(1));
        armed = true;

        await Assert.ThrowsAsync<Task6InjectedFailure>(() =>
            store.AgeMaintenance.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)));
        armed = false;
        JournalOperationPurgeResult retry = await store.AgeMaintenance.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal(1, retry.DeletedCount);
    }

    private static Task SetSqliteCommittedAgeAsync(Task6SqliteScope scope, int identity, string modifier)
        => scope.Database.ExecuteAsync(
            scope.Path,
            """
            UPDATE journal_operation
            SET committed_at_utc = CAST((julianday('now', $age) - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = $id;
            """,
            ("$age", (object)modifier),
            ("$id", MutationJournalTask6TestSupport.Identity(identity).OperationId.ToString("D")));

    private static Task SetSqliteRetentionAgeAsync(Task6SqliteScope scope, int identity, string modifier)
        => scope.Database.ExecuteAsync(
            scope.Path,
            """
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now', $age) - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = $id;
            """,
            ("$age", (object)modifier),
            ("$id", MutationJournalTask6TestSupport.Identity(identity).OperationId.ToString("D")));

    private static async Task AssertSqlServerDatabaseClockPurgeAsync(int processClockDays)
    {
        using var scope = new Task6SqlServerScope();
        var processClock = new Task6ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(processClockDays));
        SqlServerJournalPrefixStore store = scope.Open(retryHorizon: RetryHorizon, clock: processClock);
        JournalInitializeResult initialized = await store.InitializeAsync(Initialization(1));
        DateTimeOffset initialRetention = await ReadSqlServerRetentionStartedAsync(
            scope.ConnectionString,
            store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId));
        Assert.InRange(initialRetention, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(processClock.GetUtcNow(), initialized.Receipt!.CommittedAtUtc);
        await store.CommitAsync(Commit(2, 0, new byte[] { 2 }, 2));
        await SetSqlServerOperationAgeAsync(scope.ConnectionString, store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId), -120);
        await SetSqlServerOperationAgeAsync(scope.ConnectionString, store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(2).OperationId), -30);

        JournalOperationPurgeResult result = await store.AgeMaintenance.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal((1, 1, 0), (result.ScannedCount, result.DeletedCount, result.IneligibleCount));
        Assert.InRange(result.EvaluatedAtUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(result.EvaluatedAtUtc - RetryHorizon, result.EffectiveCutoffUtc);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }

    [Fact]
    public void Age_purge_rejects_negative_age_and_nonpositive_batch_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalOperationAgePurge(TimeSpan.FromTicks(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalOperationAgePurge(TimeSpan.Zero, 0));
    }

    [Fact]
    public void Age_maintenance_remains_an_opt_in_capability()
    {
        IMutationJournalMaintenance legacy = new LegacyMaintenance();

        Assert.False(legacy is IMutationJournalAgeMaintenance);
    }

    [Fact]
    public async Task Sqlite_version_one_migration_restarts_existing_receipt_retention_age()
    {
        using var database = new SqliteJournalTestDatabase();
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteMutationJournalStore.VersionOneSchemaSqlForTest);
        database.Execute(
            path,
            """
            INSERT INTO journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES ($id, 'inventory.grant', 1, zeroblob(32), 1, zeroblob(32),
                    'result.v1', 1, X'', zeroblob(32), 0);
            """,
            ("$id", (object)Guid.NewGuid().ToString("D")));

        using SqliteMutationJournalStore store = database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                MinimumRetryHorizon = RetryHorizon,
            });
        Guid rollingWriterOperation = Guid.NewGuid();
        database.Execute(
            path,
            """
            INSERT INTO journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES ($id, 'inventory.grant', 1, zeroblob(32), 1, zeroblob(32),
                    'result.v1', 1, X'', zeroblob(32), 0);
            """,
            ("$id", (object)rollingWriterOperation.ToString("D")));
        JournalOperationPurgeResult result = await store.PurgeOperationsByAgeAsync(
            new JournalOperationAgePurge(TimeSpan.Zero, 10));

        Assert.Equal(2, database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;"));
        Assert.Equal(1, database.ScalarLong(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_journal_operation_delete_guard';"));
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(2, database.ScalarLong(path, "SELECT COUNT(*) FROM journal_operation;"));
        long retentionStarted = database.ScalarLong(path, "SELECT retention_started_at_utc FROM journal_operation;");
        Assert.InRange(retentionStarted, DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
        long rollingWriterRetention = database.ScalarLong(
            path,
            $"SELECT retention_started_at_utc FROM journal_operation WHERE operation_id = '{rollingWriterOperation:D}';");
        Assert.InRange(rollingWriterRetention, DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Sqlite_validate_only_rejects_version_one_without_mutating_it()
    {
        using var database = new SqliteJournalTestDatabase();
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteMutationJournalStore.VersionOneSchemaSqlForTest);

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            }));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, exception.Kind);
        Assert.Equal(1, database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;"));
        Assert.Equal(0, database.ScalarLong(path, "SELECT COUNT(*) FROM pragma_table_info('journal_operation') WHERE name = 'retention_started_at_utc';"));
    }

    [SqlServerFact]
    public async Task Sql_server_version_one_migration_restarts_existing_receipt_retention_age()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore store = scope.Open(retryHorizon: RetryHorizon);
        await store.InitializeAsync(Initialization(1));
        Guid operationId = store.PhysicalOperationId(MutationJournalTask6TestSupport.Identity(1).OperationId);
        await DowngradeSqlServerSchemaToVersionOneAsync(scope.ConnectionString);

        try
        {
            JournalStoreException validateOnly = Assert.Throws<JournalStoreException>(() =>
                _ = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(scope.ConnectionString)
                {
                    SchemaMode = SqlServerJournalSchemaMode.ValidateOnly,
                    MinimumRetryHorizon = RetryHorizon,
                }));
            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, validateOnly.Kind);
            Assert.Equal(1, await ReadSqlServerSchemaVersionAsync(scope.ConnectionString));
            Assert.False(await SqlServerRetentionColumnExistsAsync(scope.ConnectionString));

            _ = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(scope.ConnectionString)
            {
                MinimumRetryHorizon = RetryHorizon,
            });

            await AssertSqlServerLegacyDeleteRejectedAsync(scope.ConnectionString, operationId);

            DateTimeOffset retentionStarted = await ReadSqlServerRetentionStartedAsync(scope.ConnectionString, operationId);
            Assert.InRange(retentionStarted, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
            Guid rollingWriterOperation = store.PhysicalOperationId(Guid.NewGuid());
            await InsertLegacySqlServerOperationAsync(scope.ConnectionString, rollingWriterOperation);
            DateTimeOffset rollingWriterRetention = await ReadSqlServerRetentionStartedAsync(
                scope.ConnectionString,
                rollingWriterOperation);
            Assert.InRange(rollingWriterRetention, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(10));
            await store.AgeMaintenance.PurgeOperationsByAgeAsync(
                new JournalOperationAgePurge(TimeSpan.Zero, 10));
            Assert.Equal(JournalOperationResolutionStatus.Replayed, (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        }
        finally
        {
            _ = new SqlServerMutationJournalStore(scope.ConnectionString);
        }
    }

    private static async Task SetSqlServerOperationAgeAsync(string connectionString, Guid operationId, int ageMinutes)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.journal_operation SET retention_started_at_utc = DATEADD(minute, @minutes, SYSUTCDATETIME()) WHERE operation_id = @id;";
        command.Parameters.Add("@minutes", SqlDbType.Int).Value = ageMinutes;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetSqlServerCommittedAgeAsync(string connectionString, Guid operationId, int ageMinutes)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.journal_operation SET committed_at_utc = DATEADD(minute, @minutes, SYSUTCDATETIME()) WHERE operation_id = @id;";
        command.Parameters.Add("@minutes", SqlDbType.Int).Value = ageMinutes;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertSqlServerLegacyDeleteRejectedAsync(string connectionString, Guid operationId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (SqlCommand children = connection.CreateCommand())
            {
                children.Transaction = transaction;
                children.CommandText = "DELETE FROM dbo.journal_operation_stream WHERE operation_id = @id;";
                children.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
                await children.ExecuteNonQueryAsync();
            }
            await using SqlCommand parent = connection.CreateCommand();
            parent.Transaction = transaction;
            parent.CommandText = "DELETE FROM dbo.journal_operation WHERE operation_id = @id;";
            parent.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
            await Assert.ThrowsAsync<SqlException>(() => parent.ExecuteNonQueryAsync());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task DowngradeSqlServerSchemaToVersionOneAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.trg_journal_operation_delete_guard', N'TR') IS NOT NULL
                DROP TRIGGER dbo.trg_journal_operation_delete_guard;
            DROP INDEX ix_journal_operation_retention ON dbo.journal_operation;
            ALTER TABLE dbo.journal_operation DROP COLUMN retention_started_at_utc;
            UPDATE dbo.journal_metadata SET schema_version = 1 WHERE metadata_key = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DateTimeOffset> ReadSqlServerRetentionStartedAsync(string connectionString, Guid operationId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT retention_started_at_utc FROM dbo.journal_operation WHERE operation_id = @id;";
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
        return (DateTimeOffset)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertLegacySqlServerOperationAsync(string connectionString, Guid operationId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES (@id, N'inventory.grant', 1, @hash, 1, @hash, N'result.v1', 1,
                    0x, @hash, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = new byte[32];
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<int> ReadSqlServerSchemaVersionAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM dbo.journal_metadata WHERE metadata_key = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> SqlServerRetentionColumnExistsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.journal_operation') AND name = N'retention_started_at_utc';";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private sealed class LegacyMaintenance : IMutationJournalMaintenance
    {
        public Task<JournalOperationPurgeResult> PurgeOperationsAsync(
            JournalOperationPurge purge,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Guid> RotateStoreEpochAsync(System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
