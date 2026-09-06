using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class MutationJournalCompactionTests
{
    private static readonly TimeSpan RetryHorizon = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task In_memory_snapshot_prune_and_retry_horizon_are_independent()
    {
        var clock = new Task6ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, RetryHorizon, clock);
        await AssertCompactionContractAsync(store, store, clock);
    }

    [Fact]
    public async Task Sqlite_snapshot_prune_and_retry_horizon_are_independent()
    {
        using var scope = new Task6SqliteScope();
        using SqliteMutationJournalStore store = scope.Open(retryHorizon: RetryHorizon);
        await AssertCompactionContractAsync(store, store, scope.Clock);
    }

    [SqlServerFact]
    public async Task Sql_server_snapshot_prune_and_retry_horizon_are_independent()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore store = scope.Open(retryHorizon: RetryHorizon);
        await AssertCompactionContractAsync(store, store.Maintenance, scope.Clock);
    }

    private static async Task AssertCompactionContractAsync(
        IMutationJournalStore store,
        IMutationJournalMaintenance maintenance,
        Task6ManualTimeProvider clock)
    {
        await store.InitializeAsync(Initialization(1, snapshotValue: 10));
        JournalCommit committed = Commit(2, 0, new byte[] { 1, 2, 3 }, 42);
        await store.CommitAsync(committed);

        JournalCompactionResult snapshotOnly = await store.CompactAsync(
            new JournalCompaction(StreamKey, 2, "player.v2", 2, new byte[] { 22 }, null));
        Assert.Equal(JournalCompactionStatus.Compacted, snapshotOnly.Status);
        Assert.Equal((0L, 2L, 0),
            (snapshotOnly.PreviousSnapshotVersion, snapshotOnly.SnapshotVersion, snapshotOnly.PrunedEventCount));
        Assert.Equal(new long[] { 1, 2, 3 }, (await ReadAllAsync(store)).Events.Select(value => value.StreamVersion));

        JournalCompactionResult pruned = await store.CompactAsync(
            new JournalCompaction(StreamKey, 3, "player.v3", 3, new byte[] { 33 }, 2));
        Assert.Equal((2L, 3L, 2),
            (pruned.PreviousSnapshotVersion, pruned.SnapshotVersion, pruned.PrunedEventCount));
        JournalSnapshot snapshot = (await store.LoadSnapshotAsync(StreamKey))!;
        Assert.Equal((3L, "player.v3", 3),
            (snapshot.ThroughVersion, snapshot.SnapshotSchema, snapshot.SnapshotSchemaVersion));
        Assert.Equal(new byte[] { 33 }, snapshot.Data.ToArray());
        JournalEventPage retainedTail = await ReadAllAsync(store, afterVersion: 2);
        Assert.Equal(3, Assert.Single(retainedTail.Events).StreamVersion);
        Assert.Equal(JournalEventPageStatus.SnapshotRequired, (await ReadAllAsync(store)).Status);

        JournalOperationResolution replay = await store.ResolveOperationAsync(committed.Identity);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, replay.Status);
        Assert.Equal(new byte[] { 42 }, replay.Receipt!.ResultData.ToArray());
        Assert.True(replay.Receipt.HasValidResultChecksum);

        clock.Advance(TimeSpan.FromMinutes(5));
        JournalOperationPurgeResult tooYoung = await maintenance.PurgeOperationsAsync(
            new JournalOperationPurge(clock.GetUtcNow(), 10));
        Assert.Equal(0, tooYoung.DeletedCount);
        Assert.Equal(2, tooYoung.IneligibleCount);
        Assert.Equal(JournalOperationResolutionStatus.Replayed,
            (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed,
            (await store.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
    }
}
