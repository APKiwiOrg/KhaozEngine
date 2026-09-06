using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public abstract class MutationJournalStoreConformance
{
    protected abstract MutationJournalStoreHarness CreateStore(TimeSpan? minimumRetryHorizon = null);

    [Fact]
    public async Task Initialization_creates_version_zero_exactly_once()
    {
        MutationJournalStoreHarness harness = CreateStore();
        JournalInitializeResult first = await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", snapshot: Bytes(10)));
        JournalInitializeResult second = await harness.Store.InitializeAsync(Initialization(Operation(2), "player/a", snapshot: Bytes(20)));

        Assert.Equal(JournalInitializeStatus.Initialized, first.Status);
        JournalStreamVersionRange range = Assert.Single(first.Receipt!.Streams);
        Assert.Equal((0L, 0L, 0), (range.BeforeVersion, range.AfterVersion, range.EventCount));
        Assert.Equal(JournalInitializeStatus.ExistingStream, second.Status);
        Assert.Equal(Bytes(10), (await harness.Store.LoadSnapshotAsync("player/a"))!.Data.ToArray());
    }

    [Fact]
    public async Task Identical_initialization_replays_original_receipt_and_result()
    {
        MutationJournalStoreHarness harness = CreateStore();
        JournalInitialization initialization = Initialization(Operation(1), "player/a", result: Bytes(31));

        JournalInitializeResult first = await harness.Store.InitializeAsync(initialization);
        harness.Advance(TimeSpan.FromMinutes(1));
        JournalInitializeResult replay = await harness.Store.InitializeAsync(initialization);

        Assert.Equal(JournalInitializeStatus.Replayed, replay.Status);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(first.Receipt!.CommittedAtUtc, replay.Receipt.CommittedAtUtc);
        Assert.Equal(Bytes(31), replay.Receipt.ResultData.ToArray());
    }

    [Fact]
    public async Task Different_operation_on_existing_stream_returns_existing_stream()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));

        JournalInitializeResult result = await harness.Store.InitializeAsync(Initialization(Operation(2), "player/a"));

        Assert.Equal(JournalInitializeStatus.ExistingStream, result.Status);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(2))).Status);
    }

    [Fact]
    public async Task Reused_operation_id_with_different_intent_returns_operation_conflict()
    {
        MutationJournalStoreHarness harness = CreateStore();
        JournalOperationIdentity original = Operation(1, Bytes(1));
        JournalOperationIdentity changed = Operation(1, Bytes(2));
        await harness.Store.InitializeAsync(Initialization(original, "player/a"));

        Assert.Equal(JournalInitializeStatus.OperationConflict, (await harness.Store.InitializeAsync(Initialization(changed, "player/b"))).Status);
        Assert.Equal(JournalCommitStatus.OperationConflict, (await harness.Store.CommitAsync(Commit(changed, Mutation("player/a", 0, Event(4))))).Status);
        Assert.Equal(JournalOperationResolutionStatus.OperationConflict, (await harness.Store.ResolveOperationAsync(changed)).Status);
    }

    [Fact]
    public async Task Commit_appends_contiguous_versions_and_replaces_projection_sections()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", projections: new[] { Projection("player/a", "bag", 1) }));
        JournalCommit commit = Commit(
            Operation(2),
            Mutation("player/a", 0, Event(10), Event(11)),
            projections: new[] { Projection("player/a", "bag", 2), Projection("player/a", "skills", 3) });

        JournalCommitResult result = await harness.Store.CommitAsync(commit);
        JournalEventPage events = await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024));
        JournalProjectionRead projections = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"));

        Assert.Equal(JournalCommitStatus.Applied, result.Status);
        JournalStreamVersionRange range = Assert.Single(result.Receipt!.Streams);
        Assert.Equal((0L, 2L, 2), (range.BeforeVersion, range.AfterVersion, range.EventCount));
        Assert.Equal(new long[] { 1, 2 }, events.Events.Select(value => value.StreamVersion));
        Assert.Equal(new byte[] { 10, 11 }, events.Events.Select(value => value.Payload.Span[0]));
        Assert.Equal(new[] { "bag", "skills" }, projections.Sections.Select(value => value.SectionName));
        Assert.All(projections.Sections, value => Assert.Equal(2, value.SourceVersion));
        Assert.Equal(Bytes(2), projections.Sections[0].Data.ToArray());
    }

    [Fact]
    public async Task Multi_stream_commit_is_all_or_nothing()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", projections: new[] { Projection("player/a", "bag", 1) }));
        await harness.Store.InitializeAsync(Initialization(Operation(2), "player/b"));
        JournalCommit commit = Commit(
            Operation(3),
            new[] { Mutation("player/a", 0, Event(2)), Mutation("player/b", 7, Event(3)) },
            new[] { Projection("player/a", "bag", 9) });

        JournalCommitResult result = await harness.Store.CommitAsync(commit);

        Assert.Equal(JournalCommitStatus.VersionConflict, result.Status);
        Assert.Empty((await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        Assert.Equal(Bytes(1), Assert.Single((await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections).Data.ToArray());
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(3))).Status);
    }

    [Fact]
    public async Task Version_conflict_and_missing_stream_write_nothing()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));

        JournalCommitResult stale = await harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 1, Event(1))));
        JournalCommitResult missing = await harness.Store.CommitAsync(Commit(Operation(3), Mutation("player/missing", 0, Event(2))));

        Assert.Equal(JournalCommitStatus.VersionConflict, stale.Status);
        Assert.Equal(JournalCommitStatus.VersionConflict, missing.Status);
        Assert.Empty((await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        Assert.Equal(JournalEventPageStatus.NotFound, (await harness.Store.ReadEventsAsync(new JournalEventRead("player/missing", 0, null, 10, 1024))).Status);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(2))).Status);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(3))).Status);
    }

    [Fact]
    public async Task Eventless_constraints_do_not_advance_heads_and_cannot_write_projections()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));
        JournalCommitResult result = await harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0)));

        JournalStreamVersionRange range = Assert.Single(result.Receipt!.Streams);
        Assert.Equal((0L, 0L, 0), (range.BeforeVersion, range.AfterVersion, range.EventCount));
        Assert.Equal(0, (await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).HeadVersion);
        Assert.Throws<ArgumentException>(() => Commit(Operation(3), Mutation("player/a", 0), projections: new[] { Projection("player/a", "bag", 1) }));
    }

    [Fact]
    public async Task Matching_identity_replays_original_execution_and_result()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));
        JournalOperationIdentity identity = Operation(2, Bytes(7));
        JournalCommitResult applied = await harness.Store.CommitAsync(Commit(identity, Mutation("player/a", 0, Event(4)), result: Bytes(40)));

        JournalCommitResult replay = await harness.Store.CommitAsync(Commit(identity, Mutation("player/a", 99, Event(9)), result: Bytes(90)));

        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(applied.Receipt!.CommittedAtUtc, replay.Receipt.CommittedAtUtc);
        Assert.Equal(Bytes(40), replay.Receipt.ResultData.ToArray());
        Assert.Equal(1, Assert.Single(replay.Receipt.Streams).AfterVersion);
    }

    [Fact]
    public async Task Every_replay_path_verifies_the_stored_result_checksum()
    {
        MutationJournalStoreHarness resolveHarness = CreateStore();
        JournalInitializeResult initialized = await resolveHarness.Store.InitializeAsync(Initialization(Operation(1), "player/a", result: Bytes(4)));
        await resolveHarness.CorruptStoredResultAsync(initialized.Receipt!.OperationId);
        await AssertCorrupt(() => resolveHarness.Store.ResolveOperationAsync(Operation(1)));

        MutationJournalStoreHarness initializeHarness = CreateStore();
        JournalInitialization initialization = Initialization(Operation(2), "player/b", result: Bytes(5));
        JournalInitializeResult first = await initializeHarness.Store.InitializeAsync(initialization);
        await initializeHarness.CorruptStoredResultAsync(first.Receipt!.OperationId);
        await AssertCorrupt(() => initializeHarness.Store.InitializeAsync(initialization));

        MutationJournalStoreHarness commitHarness = CreateStore();
        await commitHarness.Store.InitializeAsync(Initialization(Operation(3), "player/c"));
        JournalCommit commit = Commit(Operation(4), Mutation("player/c", 0, Event(1)), result: Bytes(6));
        JournalCommitResult committed = await commitHarness.Store.CommitAsync(commit);
        await commitHarness.CorruptStoredResultAsync(committed.Receipt!.OperationId);
        await AssertCorrupt(() => commitHarness.Store.CommitAsync(commit));
    }

    [Fact]
    public async Task Snapshot_and_ordered_pages_reconstruct_stream_and_pruned_prefix_requires_snapshot()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", snapshot: Bytes(0)));
        await harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(1), Event(2), Event(3))));

        JournalEventPage first = await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 1, 1024));
        JournalEventPage second = await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 1, first.ThroughVersion, 2, 1024));
        await harness.Store.CompactAsync(new JournalCompaction("player/a", 2, "player.v1", 1, Bytes(12), 2));
        JournalEventPage pruned = await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024));

        Assert.Equal(3, first.ThroughVersion);
        Assert.Equal(1, Assert.Single(first.Events).StreamVersion);
        Assert.False(first.ReachedThroughVersion);
        Assert.Equal(new long[] { 2, 3 }, second.Events.Select(value => value.StreamVersion));
        Assert.True(second.ReachedThroughVersion);
        Assert.Equal(2, (await harness.Store.LoadSnapshotAsync("player/a"))!.ThroughVersion);
        Assert.Equal(JournalEventPageStatus.SnapshotRequired, pruned.Status);
    }

    [Fact]
    public async Task Projection_cursors_bind_epoch_stream_and_section_versions()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", projections: new[] { Projection("player/a", "bag", 1) }));
        await harness.Store.InitializeAsync(Initialization(Operation(2), "player/b", projections: new[] { Projection("player/b", "profile", 2) }));
        JournalProjectionRead baseline = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"));
        (Guid epoch, string streamKey, long headVersion) = JournalProjectionCursor.DecodeForTest(baseline.Cursor!);

        await harness.Store.CommitAsync(Commit(Operation(3), Mutation("player/a", 0, Event(4)), projections: new[] { Projection("player/a", "bag", 9) }));
        JournalProjectionRead delta = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a", baseline.Cursor));
        JournalProjectionRead wrongStream = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/b", baseline.Cursor));

        Assert.NotEqual(Guid.Empty, epoch);
        Assert.Equal("player/a", streamKey);
        Assert.Equal(0, headVersion);
        Assert.Equal(JournalProjectionReadStatus.Success, delta.Status);
        Assert.Equal(1, Assert.Single(delta.Sections).SourceVersion);
        Assert.Equal(JournalProjectionReadStatus.ResetRequired, wrongStream.Status);
        Assert.Equal("profile", Assert.Single(wrongStream.Sections).SectionName);
    }

    [Fact]
    public async Task Stale_epoch_cursor_requests_full_reset()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a", projections: new[] { Projection("player/a", "bag", 1) }));
        JournalProjectionRead first = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"));

        Guid rotated = await harness.Maintenance.RotateStoreEpochAsync();
        JournalProjectionRead reset = await harness.Store.ReadProjectionsAsync(new JournalProjectionQuery("player/a", first.Cursor));

        Assert.NotEqual(JournalProjectionCursor.DecodeForTest(first.Cursor!).Epoch, rotated);
        Assert.Equal(JournalProjectionReadStatus.ResetRequired, reset.Status);
        Assert.Single(reset.Sections);
        Assert.Equal(rotated, JournalProjectionCursor.DecodeForTest(reset.Cursor!).Epoch);
    }

    [Fact]
    public async Task Compaction_preserves_recoverable_snapshot_and_retained_tail()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));
        await harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(1), Event(2), Event(3))));

        JournalCompactionResult result = await harness.Store.CompactAsync(new JournalCompaction("player/a", 2, "player.v2", 2, Bytes(22), 1));
        JournalSnapshot snapshot = (await harness.Store.LoadSnapshotAsync("player/a"))!;
        JournalEventPage tail = await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 1, null, 10, 1024));

        Assert.Equal(JournalCompactionStatus.Compacted, result.Status);
        Assert.Equal(1, result.PrunedEventCount);
        Assert.Equal(2, snapshot.ThroughVersion);
        Assert.Equal(Bytes(22), snapshot.Data.ToArray());
        Assert.Equal(new long[] { 2, 3 }, tail.Events.Select(value => value.StreamVersion));
    }

    [Fact]
    public async Task Purge_enforces_retry_horizon_and_deterministic_bounded_order()
    {
        MutationJournalStoreHarness harness = CreateStore(TimeSpan.FromMinutes(10));
        await harness.Store.InitializeAsync(Initialization(Operation(3), "player/c"));
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));
        await harness.Store.InitializeAsync(Initialization(Operation(2), "player/b"));
        harness.Advance(TimeSpan.FromMinutes(20));

        JournalOperationPurgeResult first = await harness.Maintenance.PurgeOperationsAsync(new JournalOperationPurge(harness.UtcNow, 2));

        Assert.Equal((2, 2, 0), (first.ScannedCount, first.DeletedCount, first.IneligibleCount));
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(1))).Status);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(2))).Status);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await harness.Store.ResolveOperationAsync(Operation(3))).Status);

        await harness.Store.InitializeAsync(Initialization(Operation(4), "player/d"));
        JournalOperationPurgeResult second = await harness.Maintenance.PurgeOperationsAsync(new JournalOperationPurge(harness.UtcNow, 10));
        Assert.Equal(1, second.DeletedCount);
        Assert.Equal(1, second.IneligibleCount);
        Assert.Equal(JournalOperationResolutionStatus.Replayed, (await harness.Store.ResolveOperationAsync(Operation(4))).Status);
    }

    [Fact]
    public async Task Purge_removes_replay_children_before_parent_and_preserves_events()
    {
        MutationJournalStoreHarness harness = CreateStore(TimeSpan.Zero);
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));
        await harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(7))));
        harness.Advance(TimeSpan.FromSeconds(1));

        JournalOperationPurgeResult result = await harness.Maintenance.PurgeOperationsAsync(new JournalOperationPurge(harness.UtcNow, 10));

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(2))).Status);
        JournalStoredEvent retained = Assert.Single((await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        Assert.Equal(Operation(2).OperationId, retained.OperationId);
    }

    [Fact]
    public async Task Cancellation_before_work_leaves_no_rows()
    {
        MutationJournalStoreHarness harness = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"), cancellation.Token));

        Assert.Null(await harness.Store.LoadSnapshotAsync("player/a"));
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await harness.Store.ResolveOperationAsync(Operation(1))).Status);
    }

    [Fact]
    public async Task Concurrent_commits_against_one_head_are_linearizable()
    {
        MutationJournalStoreHarness harness = CreateStore();
        await harness.Store.InitializeAsync(Initialization(Operation(1), "player/a"));

        JournalCommitResult[] results = await Task.WhenAll(
            harness.Store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(2)))),
            harness.Store.CommitAsync(Commit(Operation(3), Mutation("player/a", 0, Event(3)))));

        Assert.Single(results, value => value.Status == JournalCommitStatus.Applied);
        Assert.Single(results, value => value.Status == JournalCommitStatus.VersionConflict);
        Assert.Single((await harness.Store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    private static async Task AssertCorrupt(Func<Task> action)
    {
        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(action);
        Assert.Equal(JournalStoreFailureKind.CorruptData, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, exception.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, exception.Scope);
    }

    protected static JournalOperationIdentity Operation(int suffix, byte[]? intent = null)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", intent ?? Bytes(checked((byte)suffix)));

    protected static JournalInitialization Initialization(
        JournalOperationIdentity identity,
        string streamKey,
        byte[]? snapshot = null,
        IReadOnlyList<JournalProjectionWrite>? projections = null,
        byte[]? result = null)
        => new(identity, streamKey, "player.v1", 1, snapshot ?? Array.Empty<byte>(), projections ?? Array.Empty<JournalProjectionWrite>(), "result.v1", 1, result ?? Bytes(1));

    protected static JournalCommit Commit(
        JournalOperationIdentity identity,
        JournalStreamMutation mutation,
        IReadOnlyList<JournalProjectionWrite>? projections = null,
        byte[]? result = null)
        => Commit(identity, new[] { mutation }, projections, result);

    protected static JournalCommit Commit(
        JournalOperationIdentity identity,
        IReadOnlyList<JournalStreamMutation> mutations,
        IReadOnlyList<JournalProjectionWrite>? projections = null,
        byte[]? result = null)
        => new(identity, mutations, projections ?? Array.Empty<JournalProjectionWrite>(), "result.v1", 1, result ?? Bytes(1));

    protected static JournalStreamMutation Mutation(string streamKey, long expectedVersion, params JournalEvent[] events)
        => new(streamKey, expectedVersion, events);

    protected static JournalEvent Event(byte value) => new("state.changed", 1, Bytes(value));

    protected static JournalProjectionWrite Projection(string streamKey, string sectionName, byte value)
        => new(streamKey, sectionName, "section.v1", 1, Bytes(value));

    protected static byte[] Bytes(byte value) => new[] { value };
}

public sealed class MutationJournalStoreHarness
{
    private readonly Action<TimeSpan> advance;
    private readonly Func<Guid, Task> corruptStoredResultAsync;
    private readonly Func<DateTimeOffset> getUtcNow;

    public MutationJournalStoreHarness(
        IMutationJournalStore store,
        IMutationJournalMaintenance maintenance,
        Func<DateTimeOffset> getUtcNow,
        Action<TimeSpan> advance,
        Func<Guid, Task> corruptStoredResultAsync)
    {
        Store = store;
        Maintenance = maintenance;
        this.getUtcNow = getUtcNow;
        this.advance = advance;
        this.corruptStoredResultAsync = corruptStoredResultAsync;
    }

    public IMutationJournalStore Store { get; }
    public IMutationJournalMaintenance Maintenance { get; }
    public DateTimeOffset UtcNow => getUtcNow();
    public void Advance(TimeSpan duration) => advance(duration);
    public Task CorruptStoredResultAsync(Guid operationId) => corruptStoredResultAsync(operationId);
}
