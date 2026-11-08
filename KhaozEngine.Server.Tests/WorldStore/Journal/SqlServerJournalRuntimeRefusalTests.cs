using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalRuntimeRefusalTests
{
    [Fact]
    public void Sql_server_journal_never_constructs_a_message_only_store_refusal()
    {
        var legacy = typeof(JournalStoreException).GetConstructors()
            .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(JournalStoreRefusal)));
        Assert.Empty(RefusalConstructorFence.EngineCallers([typeof(SqlServerMutationJournalStore).Assembly], legacy));
    }

    [Fact]
    public void Missing_provider_clock_uses_the_database_result_guard()
    {
        var refused = Assert.Throws<JournalStoreException>(() => SqlServerMutationJournalStore.RefuseMissingDatabaseClockForTests(null));
        AssertWholeStoreCorruption(refused, JournalStoreRefusal.ProviderClockUnavailable);
        Assert.Equal("SQL Server", refused.ProviderName);
        Assert.Equal(DateTimeOffset.UnixEpoch, SqlServerMutationJournalStore.RefuseMissingDatabaseClockForTests(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Confirmed_commit_failure_keeps_its_cause_and_dimensions()
    {
        var cause = new InvalidOperationException();
        var refused = SqlServerMutationJournalStore.RefuseConfirmedCommitFailureForTests(cause, [StreamKey]);
        Assert.Equal(JournalStoreRefusal.WriteFailedAfterRollback, refused.Reason);
        Assert.Equal(JournalOperationStage.Commit, refused.Stage);
        Assert.Equal(JournalStoreFailureKind.Unavailable, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
        Assert.Equal(StreamKey, Assert.Single(refused.StreamKeys));
        Assert.Same(cause, refused.InnerException);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replacement_limits_keep_the_boundary_and_all_content(bool count)
    {
        using var scope = new Task6SqlServerScope();
        var limits = count ? new JournalLimits(projectionSectionsPerStream: 2) : new JournalLimits(aggregateProjectionBytesPerStream: 2);
        var store = scope.Open(limits: limits);
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1), Projection("skills", 2)));
        string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(Commit(2, 0, [2], 2, Projection("extra", 3))));
        Assert.Equal(count ? JournalStoreRefusal.ProjectionSectionCountExceeded : JournalStoreRefusal.ProjectionByteCountExceeded, refused.Reason);
        Assert.Equal(scope.Prefix + StreamKey, refused.StreamKey);
        Assert.Equal(JournalStoreFailureKind.ConstraintViolation, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
        Assert.Equal(scope.Prefix + StreamKey, Assert.Single(refused.StreamKeys));
        AssertLimit(refused, count, 3, 2);
        Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(Commit(2, 0, [2], 2, Projection("bag", 3)))).Status);
        using var belowScope = new Task6SqlServerScope();
        var below = belowScope.Open(limits: limits);
        await below.InitializeAsync(Initialization(10));
        Assert.Equal(JournalCommitStatus.Applied, (await below.CommitAsync(Commit(11, 0, [11], 11, Projection("bag", 3)))).Status);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stored_limits_report_the_engine_boundary(bool count)
    {
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1));
        int sectionCount = count ? 65 : 5;
        byte[] data = new byte[count ? 1 : 2 * 1024 * 1024];
        for (int i = 0; i < sectionCount; i++)
            await ExecuteAsync(scope.ConnectionString, """
                INSERT INTO dbo.journal_projection
                    (stream_key, section_name, source_version, projection_schema, projection_schema_version, data, data_sha256, updated_at_utc, created_at_utc)
                VALUES (@stream, @section, 0, 'section.v1', 1, @data, @hash, @now, @now);
                """, ("@stream", scope.Prefix + StreamKey), ("@section", $"section{i:D2}"),
                ("@data", data), ("@hash", JournalCanonicalizer.ComputeSha256(data)), ("@now", DateTimeOffset.UnixEpoch));
        var refused = await AssertCorruptAsync(() => store.ReadProjectionsAsync(new(StreamKey)), scope.Prefix + StreamKey);
        Assert.Equal(count ? JournalStoreRefusal.ProjectionSectionCountExceeded : JournalStoreRefusal.ProjectionByteCountExceeded, refused.Reason);
        Assert.Equal(scope.Prefix + StreamKey, refused.StreamKey);
        AssertLimit(refused, count, count ? 65 : 10 * 1024 * 1024, count ? 64 : 8 * 1024 * 1024);
        await RemoveSectionAsync(sectionCount - 1);
        Assert.Equal(count ? 64 : 4, (await store.ReadProjectionsAsync(new(StreamKey))).Sections.Count);
        await RemoveSectionAsync(sectionCount - 2);
        Assert.Equal(count ? 63 : 3, (await store.ReadProjectionsAsync(new(StreamKey))).Sections.Count);
        Task RemoveSectionAsync(int index) => ExecuteAsync(scope.ConnectionString,
            "DELETE FROM dbo.journal_projection WHERE stream_key = @stream AND section_name = @section;",
            ("@stream", scope.Prefix + StreamKey), ("@section", $"section{index:D2}"));
    }

    [SqlServerTheory]
    [InlineData("DELETE s FROM dbo.journal_snapshot s JOIN inserted i ON i.stream_key = s.stream_key;", JournalStoreRefusal.StreamSnapshotMissing)]
    [InlineData("UPDATE s SET through_version = 0 FROM dbo.journal_snapshot s JOIN inserted i ON i.stream_key = s.stream_key;", JournalStoreRefusal.ReplacementSnapshotVersionMismatch)]
    [InlineData("UPDATE s SET data = 0x08 FROM dbo.journal_snapshot s JOIN inserted i ON i.stream_key = s.stream_key;", JournalStoreRefusal.ReplacementSnapshotDataMismatch)]
    [InlineData("UPDATE s SET data_sha256 = CONVERT(binary(32), 0x00) FROM dbo.journal_snapshot s JOIN inserted i ON i.stream_key = s.stream_key;", JournalStoreRefusal.SnapshotChecksumMismatch)]
    public async Task Snapshot_faults_roll_back_the_trigger_write_before_pruning(string fault, JournalStoreRefusal reason)
    {
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1, 7));
        await store.CommitAsync(Commit(2, 0, [2], 2));
        await ExecuteAsync(scope.ConnectionString, "CREATE TABLE dbo.host_journal_runtime_probe (value int NOT NULL);");
        try
        {
            // CREATE TRIGGER must be the first statement in its batch. NOCOUNT keeps UPDATE's row count intact.
            await ExecuteAsync(scope.ConnectionString, $"""
                CREATE TRIGGER dbo.host_journal_runtime_fault ON dbo.journal_snapshot AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF TRIGGER_NESTLEVEL() > 1 RETURN;
                    INSERT INTO dbo.host_journal_runtime_probe VALUES (1);
                    {fault}
                END;
                """);
            string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
            var compaction = new JournalCompaction(StreamKey, 1, "player.v1", 1, new byte[] { 9 }, 1);
            var refused = await AssertCorruptAsync(() => store.CompactAsync(compaction), scope.Prefix + StreamKey);
            Assert.Equal(reason, refused.Reason);
            Assert.Equal(scope.Prefix + StreamKey, refused.StreamKey);
            if (reason == JournalStoreRefusal.ReplacementSnapshotVersionMismatch)
            {
                Assert.Equal(1L, refused.ExpectedVersion);
                Assert.Equal(0L, refused.ActualVersion);
            }
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER dbo.host_journal_runtime_fault;");
            Assert.Equal(JournalCompactionStatus.Compacted, (await store.CompactAsync(compaction)).Status);
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER IF EXISTS dbo.host_journal_runtime_fault; DROP TABLE dbo.host_journal_runtime_probe;");
        }
    }

    [SqlServerFact]
    public async Task Missing_snapshot_refuses_compaction_until_repaired()
    {
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1, 7));
        await store.CommitAsync(Commit(2, 0, [2], 2));
        await ExecuteAsync(scope.ConnectionString, "SELECT * INTO dbo.host_journal_saved_snapshot FROM dbo.journal_snapshot WHERE stream_key = @stream; DELETE FROM dbo.journal_snapshot WHERE stream_key = @stream;", ("@stream", scope.Prefix + StreamKey));
        try
        {
            string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
            var compaction = new JournalCompaction(StreamKey, 1, "player.v1", 1, new byte[] { 9 }, 1);
            var refused = await AssertCorruptAsync(() => store.CompactAsync(compaction), scope.Prefix + StreamKey);
            Assert.Equal(JournalStoreRefusal.StreamSnapshotMissing, refused.Reason);
            Assert.Equal(scope.Prefix + StreamKey, refused.StreamKey);
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
            await ExecuteAsync(scope.ConnectionString, "INSERT INTO dbo.journal_snapshot SELECT * FROM dbo.host_journal_saved_snapshot;");
            Assert.Equal(JournalCompactionStatus.Compacted, (await store.CompactAsync(compaction)).Status);
        }
        finally { await ExecuteAsync(scope.ConnectionString, "DROP TABLE dbo.host_journal_saved_snapshot;"); }
    }

    [SqlServerFact]
    public async Task Missing_epoch_refuses_reads_until_repaired()
    {
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1));
        await ExecuteAsync(scope.ConnectionString, "SELECT * INTO dbo.host_journal_saved_metadata FROM dbo.journal_metadata; DELETE FROM dbo.journal_metadata;");
        try
        {
            var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.ReadProjectionsAsync(new(StreamKey)));
            AssertWholeStoreCorruption(refused, JournalStoreRefusal.StoredEpochInvalid);
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, "INSERT INTO dbo.journal_metadata SELECT * FROM dbo.host_journal_saved_metadata; DROP TABLE dbo.host_journal_saved_metadata;");
        }
        Assert.Equal(JournalProjectionReadStatus.Success, (await store.ReadProjectionsAsync(new(StreamKey))).Status);
    }

    [SqlServerFact]
    public async Task Unexpected_epoch_update_count_rolls_back_the_trigger_write()
    {
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await ExecuteAsync(scope.ConnectionString, "CREATE TABLE dbo.host_journal_runtime_probe (value int NOT NULL);");
        try
        {
            // Trigger rows contribute to ExecuteNonQuery, so two side rows make the result differ from one.
            await ExecuteAsync(scope.ConnectionString, """
                CREATE TRIGGER dbo.host_journal_runtime_fault ON dbo.journal_metadata AFTER UPDATE AS
                BEGIN SET NOCOUNT OFF; INSERT INTO dbo.host_journal_runtime_probe VALUES (1), (2); END;
                """);
            string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
            var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.Maintenance.RotateStoreEpochAsync());
            AssertWholeStoreCorruption(refused, JournalStoreRefusal.MetadataUpdateNotApplied);
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER dbo.host_journal_runtime_fault;");
            Assert.NotEqual(Guid.Empty, await store.Maintenance.RotateStoreEpochAsync());
        }
        finally { await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER IF EXISTS dbo.host_journal_runtime_fault; DROP TABLE dbo.host_journal_runtime_probe;"); }
    }

    private static void AssertWholeStoreCorruption(JournalStoreException refused, JournalStoreRefusal reason)
    {
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(JournalStoreFailureKind.CorruptData, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Empty(refused.StreamKeys);
    }

    private static void AssertLimit(JournalStoreException refused, bool count, long actual, long maximum)
    {
        Assert.Equal(count ? actual : null, refused.ActualCount);
        Assert.Equal(count ? maximum : null, refused.MaximumCount);
        Assert.Equal(count ? null : actual, refused.ActualBytes);
        Assert.Equal(count ? null : maximum, refused.MaximumBytes);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) SqlServerMutationJournalStore.Add(command, name, value);
        await command.ExecuteNonQueryAsync();
    }
}
