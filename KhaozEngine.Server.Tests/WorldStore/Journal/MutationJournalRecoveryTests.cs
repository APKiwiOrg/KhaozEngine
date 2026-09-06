using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class MutationJournalRecoveryTests
{
    [Fact]
    public async Task In_memory_replays_one_hundred_identical_submissions_and_later_commits()
    {
        var store = new InMemoryMutationJournalStore();
        await AssertReplayRecoveryAsync(store, () => store);
        await AssertFixedRecoveryHeadAsync(store, operationOffset: 20);
    }

    [Fact]
    public async Task Sqlite_reopen_replays_one_hundred_identical_submissions_and_later_commits()
    {
        using var scope = new Task6SqliteScope();
        SqliteMutationJournalStore writer = scope.Open();
        await AssertReplayRecoveryAsync(writer, () => scope.Open());
        writer.Dispose();
        using SqliteMutationJournalStore reopened = scope.Open();
        await AssertFixedRecoveryHeadAsync(reopened, operationOffset: 20);
    }

    [SqlServerFact]
    public async Task Sql_server_reopen_replays_one_hundred_identical_submissions_and_later_commits()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore store = scope.Open();
        await AssertReplayRecoveryAsync(store, () => scope.Open());
        await AssertFixedRecoveryHeadAsync(scope.Open(), operationOffset: 20);
    }

    [Fact]
    public async Task Executor_completion_keeps_exact_before_and_after_ranges()
    {
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(Initialization(30));
        var executor = new MutationJournalExecutor(
            store,
            new JournalExecutorOptions(1, 1, 1024 * 1024, 0, TimeSpan.Zero, TimeSpan.Zero));
        JournalCommit commit = Commit(31, 0, new byte[] { 4, 5 }, 45);

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(commit).Status);
        JournalCompletion completion = await WaitForCompletionAsync(executor);

        JournalStreamVersionRange range = Assert.Single(completion.Result!.Receipt!.Streams);
        Assert.Equal((0L, 2L, 2), (range.BeforeVersion, range.AfterVersion, range.EventCount));
        Assert.Equal(new byte[] { 45 }, completion.Result.Receipt.ResultData.ToArray());
        Assert.True(completion.Result.Receipt.HasValidResultChecksum);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task Sqlite_sequence_gap_is_reported_as_committed_corruption_after_reopen()
    {
        using var scope = new Task6SqliteScope();
        using (SqliteMutationJournalStore writer = scope.Open())
            await SeedTwoEventsAsync(writer, 40);
        scope.Database.Execute(
            scope.Path,
            "DELETE FROM journal_event WHERE stream_key = 'task6/player' AND stream_version = 1;");

        using SqliteMutationJournalStore reopened = scope.Open();
        await AssertCorruptAsync(() => ReadAllAsync(reopened));
    }

    [Fact]
    public async Task In_memory_sequence_gap_is_reported_as_committed_corruption()
    {
        var store = new InMemoryMutationJournalStore();
        await SeedTwoEventsAsync(store, 50);
        DeleteInMemoryEvent(store, index: 0);

        await AssertCorruptAsync(() => ReadAllAsync(store));
    }

    [Fact]
    public async Task In_memory_exhausted_tail_before_captured_head_is_reported_as_corruption()
    {
        var store = new InMemoryMutationJournalStore();
        await SeedTwoEventsAsync(store, 55);
        DeleteInMemoryEvent(store, index: 1);

        await AssertCorruptAsync(() => ReadAllAsync(store));
    }

    [SqlServerFact]
    public async Task Sql_server_sequence_gap_is_reported_as_committed_corruption_after_reopen()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore writer = scope.Open();
        await SeedTwoEventsAsync(writer, 60);
        await DeleteSqlServerEventAsync(scope, streamVersion: 1);

        await AssertCorruptAsync(() => ReadAllAsync(scope.Open()));
    }

    [Fact]
    public async Task Page_count_and_byte_limits_do_not_report_false_sequence_corruption()
    {
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(Initialization(70));
        await store.CommitAsync(Commit(71, 0, new byte[] { 1, 2, 3 }, 3));

        JournalEventPage countLimited = await store.ReadEventsAsync(
            new JournalEventRead(StreamKey, 0, null, 1, 1024));
        JournalEventPage byteLimited = await store.ReadEventsAsync(
            new JournalEventRead(StreamKey, 0, null, 10, 1));

        Assert.Equal(1, Assert.Single(countLimited.Events).StreamVersion);
        Assert.False(countLimited.ReachedThroughVersion);
        Assert.Equal(1, Assert.Single(byteLimited.Events).StreamVersion);
        Assert.False(byteLimited.ReachedThroughVersion);
    }

    [Fact]
    public async Task In_memory_corrupt_snapshot_refuses_recovery_with_stream_scope()
    {
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(Initialization(80, snapshotValue: 8));
        CorruptInMemorySnapshot(store);

        await AssertCorruptAsync(() => store.LoadSnapshotAsync(StreamKey));
    }

    [Fact]
    public async Task Sqlite_corrupt_snapshot_refuses_recovery_after_reopen()
    {
        using var scope = new Task6SqliteScope();
        using (SqliteMutationJournalStore writer = scope.Open())
            await writer.InitializeAsync(Initialization(90, snapshotValue: 9));
        scope.Database.Execute(
            scope.Path,
            "PRAGMA ignore_check_constraints = ON; UPDATE journal_snapshot SET data_sha256 = zeroblob(32) WHERE stream_key = 'task6/player';");

        using SqliteMutationJournalStore reopened = scope.Open();
        await AssertCorruptAsync(() => reopened.LoadSnapshotAsync(StreamKey));
    }

    [SqlServerFact]
    public async Task Sql_server_corrupt_snapshot_refuses_recovery_after_reopen()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore writer = scope.Open();
        await writer.InitializeAsync(Initialization(100, snapshotValue: 10));
        await SqlServerJournalTestDatabase.CorruptSnapshotChecksumAsync(
            scope.ConnectionString,
            scope.Prefix + StreamKey);

        await AssertCorruptAsync(() => scope.Open().LoadSnapshotAsync(StreamKey));
    }

    private static async Task AssertReplayRecoveryAsync(
        IMutationJournalStore writer,
        Func<IMutationJournalStore> reopen)
    {
        await writer.InitializeAsync(Initialization(1));
        JournalOperationIdentity identity = MutationJournalTask6TestSupport.Identity(2, new byte[] { 2 });
        JournalCommit original = Commit(identity, 0, new byte[] { 7 }, 41, Projection("bag", 9));
        var results = new List<JournalCommitResult>(100);
        for (int attempt = 0; attempt < 100; attempt++)
            results.Add(await writer.CommitAsync(original));

        Assert.Single(results, result => result.Status == JournalCommitStatus.Applied);
        Assert.Equal(99, results.Count(result => result.Status == JournalCommitStatus.Replayed));
        JournalCommitReceipt firstReceipt = results[0].Receipt!;
        Assert.True(firstReceipt.HasValidResultChecksum);
        JournalStreamVersionRange firstRange = Assert.Single(firstReceipt.Streams);
        Assert.Equal((0L, 1L, 1), (firstRange.BeforeVersion, firstRange.AfterVersion, firstRange.EventCount));
        Assert.All(results, result => Assert.Equal(new byte[] { 41 }, result.Receipt!.ResultData.ToArray()));

        IMutationJournalStore recovered = reopen();
        JournalOperationResolution resolution = await recovered.ResolveOperationAsync(identity);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, resolution.Status);
        Assert.Equal(firstReceipt.CommittedAtUtc, resolution.Receipt!.CommittedAtUtc);
        Assert.Equal(new byte[] { 41 }, resolution.Receipt.ResultData.ToArray());
        Assert.True(resolution.Receipt.HasValidResultChecksum);

        JournalCommit rebuilt = Commit(identity, 99, new byte[] { 99, 100 }, 99, Projection("bag", 99));
        JournalCommitResult rebuiltReplay = await recovered.CommitAsync(rebuilt);
        Assert.Equal(JournalCommitStatus.Replayed, rebuiltReplay.Status);
        Assert.Equal(new byte[] { 41 }, rebuiltReplay.Receipt!.ResultData.ToArray());

        JournalOperationIdentity changedIntent = MutationJournalTask6TestSupport.Identity(2, new byte[] { 3 });
        JournalCommitResult conflict = await recovered.CommitAsync(
            Commit(changedIntent, 1, new byte[] { 55 }, 55));
        Assert.Equal(JournalCommitStatus.OperationConflict, conflict.Status);

        JournalCommitResult later = await recovered.CommitAsync(Commit(3, 1, new byte[] { 8 }, 42));
        Assert.Equal(JournalCommitStatus.Applied, later.Status);
        JournalStreamVersionRange laterRange = Assert.Single(later.Receipt!.Streams);
        Assert.Equal((1L, 2L, 1), (laterRange.BeforeVersion, laterRange.AfterVersion, laterRange.EventCount));
        JournalOperationResolution lateReplay = await recovered.ResolveOperationAsync(identity);
        Assert.Equal(new byte[] { 41 }, lateReplay.Receipt!.ResultData.ToArray());
        JournalStreamVersionRange replayRange = Assert.Single(lateReplay.Receipt.Streams);
        Assert.Equal((0L, 1L), (replayRange.BeforeVersion, replayRange.AfterVersion));

        JournalEventPage page = await ReadAllAsync(recovered);
        Assert.Equal(new byte[] { 7, 8 }, page.Events.Select(value => value.Payload.Span[0]).ToArray());
        Assert.Equal(new byte[] { 9 }, Assert.Single(
            (await recovered.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).Sections).Data.ToArray());
    }

    private static async Task AssertFixedRecoveryHeadAsync(IMutationJournalStore store, int operationOffset)
    {
        JournalEventPage first = await store.ReadEventsAsync(
            new JournalEventRead(StreamKey, 0, null, 1, 1024));
        Assert.Equal(2, first.ThroughVersion);

        await store.CommitAsync(Commit(operationOffset, 2, new byte[] { 9 }, 9));
        JournalEventPage second = await store.ReadEventsAsync(
            new JournalEventRead(StreamKey, 1, first.ThroughVersion, 10, 1024));

        Assert.Equal(2, second.ThroughVersion);
        Assert.Equal(2, Assert.Single(second.Events).StreamVersion);
        Assert.True(second.ReachedThroughVersion);
        Assert.Equal(3, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
    }

    private static async Task SeedTwoEventsAsync(IMutationJournalStore store, int operationOffset)
    {
        await store.InitializeAsync(Initialization(operationOffset));
        await store.CommitAsync(Commit(operationOffset + 1, 0, new byte[] { 1, 2 }, 2));
    }

    private static void DeleteInMemoryEvent(InMemoryMutationJournalStore store, int index)
    {
        object state = typeof(InMemoryMutationJournalStore)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        object streams = state.GetType().GetProperty("Streams")!.GetValue(state)!;
        object stream = streams.GetType().GetProperty("Item")!.GetValue(streams, new object[] { StreamKey })!;
        var events = (IList)stream.GetType().GetProperty("Events")!.GetValue(stream)!;
        events.RemoveAt(index);
    }

    private static void CorruptInMemorySnapshot(InMemoryMutationJournalStore store)
    {
        object state = typeof(InMemoryMutationJournalStore)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        object streams = state.GetType().GetProperty("Streams")!.GetValue(state)!;
        object stream = streams.GetType().GetProperty("Item")!.GetValue(streams, new object[] { StreamKey })!;
        var snapshot = (JournalSnapshot)stream.GetType().GetProperty("Snapshot")!.GetValue(stream)!;
        var checksum = (byte[])typeof(JournalSnapshot)
            .GetField("dataChecksum", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(snapshot)!;
        checksum[0] ^= 0xff;
    }

    private static async Task DeleteSqlServerEventAsync(Task6SqlServerScope scope, long streamVersion)
    {
        await using var connection = new SqlConnection(scope.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.journal_event WHERE stream_key = @stream AND stream_version = @version;";
        command.Parameters.AddWithValue("@stream", scope.Prefix + StreamKey);
        command.Parameters.AddWithValue("@version", streamVersion);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
