using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteJournalRuntimeRefusalTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public void Sqlite_journal_never_constructs_a_message_only_store_refusal()
    {
        var legacy = typeof(JournalStoreException).GetConstructors()
            .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(JournalStoreRefusal)));
        Assert.Empty(RefusalConstructorFence.EngineCallers([typeof(SqliteMutationJournalStore).Assembly], legacy));
    }

    [Fact]
    public async Task Cancellation_inside_a_write_keeps_the_cause_and_rolls_back_every_row()
    {
        string path = database.NewPath();
        bool armed = false;
        var cause = new OperationCanceledException();
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.BeforeCommit) throw cause;
        });
        using var store = database.Open(path, hook: hook);
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1)));
        var before = SqliteJournalFileState.Capture(path);
        JournalCommit commit = Commit(2, 0, [2], 2, Projection("bag", 2));
        armed = true;
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(commit));
        Assert.Equal(JournalStoreRefusal.OperationCancelled, refused.Reason);
        Assert.Equal(JournalOperationStage.Transaction, refused.Stage);
        Assert.Equal(JournalStoreFailureKind.Cancelled, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
        Assert.Equal(StreamKey, Assert.Single(refused.StreamKeys));
        Assert.Same(cause, refused.InnerException);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        armed = false;
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(commit)).Status);
    }

    [Theory]
    [InlineData(false, JournalStoreRefusal.ProjectionByteCountExceeded)]
    [InlineData(true, JournalStoreRefusal.ProjectionSectionCountExceeded)]
    public async Task Replacement_limits_keep_the_configured_boundary_and_all_content(bool count, JournalStoreRefusal reason)
    {
        string path = database.NewPath();
        var limits = count ? new JournalLimits(projectionSectionsPerStream: 2) : new JournalLimits(aggregateProjectionBytesPerStream: 2);
        using var store = database.Open(path, new(database.ConnectionString(path)) { Limits = limits });
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1), Projection("skills", 2)));
        var before = SqliteJournalFileState.Capture(path);
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(Commit(2, 0, [2], 2, Projection("extra", 3))));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        Assert.Equal(JournalStoreFailureKind.ConstraintViolation, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
        Assert.Equal(StreamKey, Assert.Single(refused.StreamKeys));
        AssertLimit(refused, count, 3, 2);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(Commit(2, 0, [2], 2, Projection("bag", 3)))).Status);
        string belowPath = database.NewPath();
        using var below = database.Open(belowPath, new(database.ConnectionString(belowPath)) { Limits = limits });
        await below.InitializeAsync(Initialization(10));
        Assert.Equal(JournalCommitStatus.Applied, (await below.CommitAsync(Commit(11, 0, [11], 11, Projection("bag", 3)))).Status);
    }

    [Theory]
    [InlineData(false, JournalStoreRefusal.ProjectionByteCountExceeded)]
    [InlineData(true, JournalStoreRefusal.ProjectionSectionCountExceeded)]
    public async Task Stored_projection_limits_report_the_engine_boundary(bool count, JournalStoreRefusal reason)
    {
        string path = database.NewPath();
        using var store = database.Open(path);
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1)));
        int sectionCount = count ? 65 : 5;
        int sectionBytes = count ? 1 : 2 * 1024 * 1024;
        byte[] data = new byte[sectionBytes];
        byte[] hash = JournalCanonicalizer.ComputeSha256(data);
        for (int i = 0; i < sectionCount; i++)
            database.Execute(path, """
                INSERT OR REPLACE INTO journal_projection
                    (stream_key, section_name, source_version, projection_schema, projection_schema_version, data, data_sha256, updated_at_utc)
                VALUES ($stream, $section, 0, 'bag.v1', 1, $data, $hash, 0);
                """, ("$stream", StreamKey), ("$section", i == 0 ? "bag" : $"section{i:D2}"), ("$data", data), ("$hash", hash));
        var refused = await AssertCorruptAsync(() => store.ReadProjectionsAsync(new(StreamKey)));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        AssertLimit(refused, count, count ? 65 : 10 * 1024 * 1024, count ? 64 : 8 * 1024 * 1024);
        database.Execute(path, "DELETE FROM journal_projection WHERE section_name = $section;", ("$section", $"section{sectionCount - 1:D2}"));
        JournalProjectionRead atLimit = await store.ReadProjectionsAsync(new(StreamKey));
        Assert.Equal(count ? 64 : 4, atLimit.Sections.Count);
        database.Execute(path, "DELETE FROM journal_projection WHERE section_name = $section;", ("$section", $"section{sectionCount - 2:D2}"));
        Assert.Equal(count ? 63 : 3, (await store.ReadProjectionsAsync(new(StreamKey))).Sections.Count);
    }

    [Theory]
    [InlineData("DELETE FROM journal_snapshot WHERE stream_key = NEW.stream_key;", JournalStoreRefusal.StreamSnapshotMissing)]
    [InlineData("UPDATE journal_snapshot SET through_version = 0 WHERE stream_key = NEW.stream_key;", JournalStoreRefusal.ReplacementSnapshotVersionMismatch)]
    [InlineData("UPDATE journal_snapshot SET data_sha256 = zeroblob(32) WHERE stream_key = NEW.stream_key;", JournalStoreRefusal.SnapshotChecksumMismatch)]
    public async Task Replacement_snapshot_faults_roll_back_the_trigger_write_before_pruning(string fault, JournalStoreRefusal reason)
    {
        string path = database.NewPath();
        using var store = database.Open(path);
        await store.InitializeAsync(Initialization(1, 7));
        await store.CommitAsync(Commit(2, 0, [2], 2));
        // Added after opening the validated store. The side row would survive a missing rollback even for deletion.
        database.Execute(path, $"""
            CREATE TABLE host_probe(value INTEGER);
            CREATE TRIGGER host_snapshot_fault AFTER UPDATE ON journal_snapshot
            BEGIN
                INSERT INTO host_probe VALUES (1);
                {fault}
            END;
            """);
        var before = SqliteJournalFileState.Capture(path);
        var compaction = new JournalCompaction(StreamKey, 1, "player.v1", 1, new byte[] { 9 }, 1);
        var refused = await AssertCorruptAsync(() => store.CompactAsync(compaction));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        if (reason == JournalStoreRefusal.ReplacementSnapshotVersionMismatch)
        {
            Assert.Equal(1, refused.ExpectedVersion);
            Assert.Equal(0, refused.ActualVersion);
        }
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "DROP TRIGGER host_snapshot_fault;");
        Assert.Equal(JournalCompactionStatus.Compacted, (await store.CompactAsync(compaction)).Status);
        Assert.Equal(new byte[] { 9 }, (await store.LoadSnapshotAsync(StreamKey))!.Data.ToArray());
    }

    [Fact]
    public async Task Missing_stream_snapshot_refuses_compaction_until_repaired()
    {
        string path = database.NewPath();
        using var store = database.Open(path);
        await store.InitializeAsync(Initialization(1, 7));
        await store.CommitAsync(Commit(2, 0, [2], 2));
        database.Execute(path, "CREATE TABLE host_saved_snapshot AS SELECT * FROM journal_snapshot; DELETE FROM journal_snapshot;");
        var before = SqliteJournalFileState.Capture(path);
        var compaction = new JournalCompaction(StreamKey, 1, "player.v1", 1, new byte[] { 9 }, 1);
        var refused = await AssertCorruptAsync(() => store.CompactAsync(compaction));
        Assert.Equal(JournalStoreRefusal.StreamSnapshotMissing, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "INSERT INTO journal_snapshot SELECT * FROM host_saved_snapshot;");
        Assert.Equal(JournalCompactionStatus.Compacted, (await store.CompactAsync(compaction)).Status);
    }

    [Fact]
    public async Task Invalid_epoch_refuses_reads_until_repaired()
    {
        string path = database.NewPath();
        using var store = database.Open(path);
        await store.InitializeAsync(Initialization(1, 7));
        string epoch = database.ScalarText(path, "SELECT store_epoch FROM journal_metadata;");
        database.Execute(path, "UPDATE journal_metadata SET store_epoch = 'gggggggggggggggggggggggggggggggg';");
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.ReadProjectionsAsync(new(StreamKey)));
        AssertWholeStoreCorruption(refused, JournalStoreRefusal.StoredEpochInvalid);
        database.Execute(path, "UPDATE journal_metadata SET store_epoch = $epoch;", ("$epoch", epoch));
        Assert.Equal(JournalProjectionReadStatus.Success, (await store.ReadProjectionsAsync(new(StreamKey))).Status);
    }

    [Fact]
    public async Task Ignored_epoch_update_refuses_and_rolls_back_the_trigger_write()
    {
        string path = database.NewPath();
        using var store = database.Open(path);
        database.Execute(path, """
            CREATE TABLE host_probe(value INTEGER);
            CREATE TRIGGER host_ignore_epoch BEFORE UPDATE ON journal_metadata
            BEGIN INSERT INTO host_probe VALUES (1); SELECT RAISE(IGNORE); END;
            """);
        var before = SqliteJournalFileState.Capture(path);
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.RotateStoreEpochAsync());
        AssertWholeStoreCorruption(refused, JournalStoreRefusal.MetadataUpdateNotApplied);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "DROP TRIGGER host_ignore_epoch;");
        Assert.NotEqual(Guid.Empty, await store.RotateStoreEpochAsync());
    }

    [Fact]
    public void Missing_provider_clock_uses_the_same_guard_as_the_database_result()
    {
        var refused = Assert.Throws<JournalStoreException>(() => SqliteMutationJournalStore.RefuseMissingDatabaseClockForTests(null));
        AssertWholeStoreCorruption(refused, JournalStoreRefusal.ProviderClockUnavailable);
        Assert.Equal("SQLite", refused.ProviderName);
        Assert.Equal(DateTimeOffset.UnixEpoch, SqliteMutationJournalStore.RefuseMissingDatabaseClockForTests(0L));
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
        if (count)
        {
            Assert.Equal(actual, refused.ActualCount);
            Assert.Equal(maximum, refused.MaximumCount);
        }
        else
        {
            Assert.Equal(actual, refused.ActualBytes);
            Assert.Equal(maximum, refused.MaximumBytes);
        }
    }

    public void Dispose() => database.Dispose();
}
