using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteMutationJournalFailureTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public Task Failure_after_event_writes_rolls_back_all_mutation_rows()
        => InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase.AfterEventWrites);

    [Fact]
    public Task Failure_after_projection_writes_rolls_back_all_mutation_rows()
        => InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase.AfterProjectionWrites);

    private async Task InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase failurePhase)
    {
        string path = database.NewPath();
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == failurePhase) throw new InjectedJournalFailure();
        });
        using SqliteMutationJournalStore store = database.Open(path, hook: hook);
        await store.InitializeAsync(Initialization(1));
        armed = true;
        JournalCommit commit = Commit(2);

        await Assert.ThrowsAsync<InjectedJournalFailure>(() => store.CommitAsync(commit));

        Assert.Empty((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        Assert.Empty((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(commit.Identity)).Status);
    }

    [Fact]
    public async Task Busy_immediate_transaction_maps_to_timeout_and_definitely_not_committed()
    {
        string path = database.NewPath();
        var options = new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
        {
            BusyTimeout = TimeSpan.FromMilliseconds(20),
        };
        using SqliteMutationJournalStore store = database.Open(path, options);
        await store.InitializeAsync(Initialization(1));
        using var locker = new SqliteConnection(database.ConnectionString(path));
        locker.Open();
        using SqliteTransaction held = locker.BeginTransaction(deferred: false);

        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(Commit(2)));

        Assert.Equal(JournalStoreFailureKind.Timeout, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, exception.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, exception.Scope);
        Assert.Equal(new[] { "player/a" }, exception.StreamKeys);
    }

    [Fact]
    public async Task Failure_after_commit_maps_to_unknown_outcome_and_same_id_replays()
    {
        string path = database.NewPath();
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.AfterCommitBeforeResponse) throw new InjectedJournalFailure();
        });
        using SqliteMutationJournalStore store = database.Open(path, hook: hook);
        await store.InitializeAsync(Initialization(1));
        armed = true;
        JournalCommit commit = Commit(2);

        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(commit));
        armed = false;
        JournalCommitResult replay = await store.CommitAsync(commit);

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, exception.Certainty);
        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.Single((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    [Theory]
    [InlineData(false, JournalInitializeStatus.Replayed)]
    [InlineData(true, JournalInitializeStatus.OperationConflict)]
    public async Task Initialize_operation_insert_collision_rolls_back_and_resolves_authoritative_row(
        bool mismatch,
        JournalInitializeStatus expectedStatus)
    {
        string path = database.NewPath();
        JournalInitialization committed = Initialization(20);
        using (SqliteMutationJournalStore seed = database.Open(path))
            await seed.InitializeAsync(committed);
        var hook = new SqliteJournalTestHook(_ => { }, suppressedOperationLookups: 1);
        using SqliteMutationJournalStore racing = database.Open(path, hook: hook);
        JournalOperationIdentity identity = mismatch
            ? new JournalOperationIdentity(committed.Identity.OperationId, "world/account", "bank.deposit", new byte[] { 99 })
            : committed.Identity;
        var attempt = new JournalInitialization(
            identity,
            "player/b",
            "player.v1",
            1,
            new byte[] { 2 },
            new[] { new JournalProjectionWrite("player/b", "bag", "bag.v1", 1, new byte[] { 3 }) },
            "result.v1",
            1,
            new byte[] { 4 });

        JournalInitializeResult result = await racing.InitializeAsync(attempt);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(await racing.LoadSnapshotAsync("player/b"));
        Assert.Equal(
            JournalProjectionReadStatus.NotFound,
            (await racing.ReadProjectionsAsync(new JournalProjectionQuery("player/b"))).Status);
        if (!mismatch) Assert.Equal(committed.ResultData.ToArray(), result.Receipt!.ResultData.ToArray());
    }

    [Theory]
    [InlineData(false, JournalCommitStatus.Replayed)]
    [InlineData(true, JournalCommitStatus.OperationConflict)]
    public async Task Commit_operation_insert_collision_rolls_back_and_resolves_authoritative_row(
        bool mismatch,
        JournalCommitStatus expectedStatus)
    {
        string path = database.NewPath();
        using (SqliteMutationJournalStore seed = database.Open(path))
        {
            await seed.InitializeAsync(Initialization(30));
            await seed.InitializeAsync(new JournalInitialization(
                Identity(31), "player/b", "player.v1", 1, Array.Empty<byte>(),
                Array.Empty<JournalProjectionWrite>(), "result.v1", 1, new byte[] { 1 }));
            await seed.CommitAsync(Commit(32));
        }
        var hook = new SqliteJournalTestHook(_ => { }, suppressedOperationLookups: 1);
        using SqliteMutationJournalStore racing = database.Open(path, hook: hook);
        JournalOperationIdentity identity = mismatch
            ? new JournalOperationIdentity(Identity(32).OperationId, "world/account", "bank.deposit", new byte[] { 99 })
            : Identity(32);
        var attempt = new JournalCommit(
            identity,
            new[] { new JournalStreamMutation("player/b", 0, new[] { new JournalEvent("state.changed", 1, new byte[] { 8 }) }) },
            new[] { new JournalProjectionWrite("player/b", "bag", "bag.v1", 1, new byte[] { 9 }) },
            "result.v1",
            1,
            new byte[] { 10 });

        JournalCommitResult result = await racing.CommitAsync(attempt);

        Assert.Equal(expectedStatus, result.Status);
        JournalEventPage page = await racing.ReadEventsAsync(new JournalEventRead("player/b", 0, null, 10, 1024));
        Assert.Equal(0, page.ThroughVersion);
        Assert.Empty(page.Events);
        Assert.Empty((await racing.ReadProjectionsAsync(new JournalProjectionQuery("player/b"))).Sections);
        if (!mismatch) Assert.Equal(new byte[] { 41 }, result.Receipt!.ResultData.ToArray());
    }

    [Fact]
    public async Task Commit_invokes_shared_mutation_boundaries_in_order()
    {
        string path = database.NewPath();
        var phases = new List<JournalTestHookPhase>();
        var hook = new SqliteJournalTestHook(phases.Add);
        using SqliteMutationJournalStore store = database.Open(path, hook: hook);
        await store.InitializeAsync(Initialization(1));
        phases.Clear();

        await store.CommitAsync(Commit(2));

        Assert.Equal(
            new[]
            {
                JournalTestHookPhase.BeforeTransaction,
                JournalTestHookPhase.AfterOperationResolution,
                JournalTestHookPhase.AfterHeadValidation,
                JournalTestHookPhase.AfterEventWrites,
                JournalTestHookPhase.AfterProjectionWrites,
                JournalTestHookPhase.BeforeCommit,
                JournalTestHookPhase.AfterCommitBeforeResponse,
            },
            phases);
    }

    [Fact]
    public async Task Malformed_stored_checksums_map_to_committed_data_corruption()
    {
        string snapshotPath = database.NewPath();
        using SqliteMutationJournalStore snapshotStore = database.Open(snapshotPath);
        await snapshotStore.InitializeAsync(Initialization(1, new byte[] { 3 }));
        database.Execute(snapshotPath, "PRAGMA ignore_check_constraints = ON; UPDATE journal_snapshot SET data_sha256 = X'00';");
        await AssertCorrupt(() => snapshotStore.LoadSnapshotAsync("player/a"));

        string eventPath = database.NewPath();
        using SqliteMutationJournalStore eventStore = database.Open(eventPath);
        await eventStore.InitializeAsync(Initialization(1));
        await eventStore.CommitAsync(Commit(2));
        database.Execute(eventPath, "PRAGMA ignore_check_constraints = ON; UPDATE journal_event SET payload_sha256 = X'00';");
        await AssertCorrupt(() => eventStore.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024)));

        string projectionPath = database.NewPath();
        using SqliteMutationJournalStore projectionStore = database.Open(projectionPath);
        await projectionStore.InitializeAsync(Initialization(1));
        await projectionStore.CommitAsync(Commit(2));
        database.Execute(projectionPath, "PRAGMA ignore_check_constraints = ON; UPDATE journal_projection SET data_sha256 = X'00';");
        await AssertCorrupt(() => projectionStore.ReadProjectionsAsync(new JournalProjectionQuery("player/a")));

        string operationPath = database.NewPath();
        using SqliteMutationJournalStore operationStore = database.Open(operationPath);
        JournalInitialization initialization = Initialization(1);
        await operationStore.InitializeAsync(initialization);
        database.Execute(operationPath, "PRAGMA ignore_check_constraints = ON; UPDATE journal_operation SET result_sha256 = X'00';");
        await AssertCorrupt(() => operationStore.ResolveOperationAsync(initialization.Identity));
    }

    [Fact]
    public Task Failure_after_snapshot_write_preserves_prior_snapshot_and_tail()
        => CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase.SnapshotWrittenBeforeVerification);

    [Fact]
    public Task Failure_before_event_prune_preserves_prior_snapshot_and_tail()
        => CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase.SnapshotVerifiedBeforePrune);

    private async Task CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase failurePhase)
    {
        string path = database.NewPath();
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == failurePhase) throw new InjectedJournalFailure();
        });
        using SqliteMutationJournalStore store = database.Open(path, hook: hook);
        await store.InitializeAsync(Initialization(1, new byte[] { 10 }));
        await store.CommitAsync(Commit(2, projections: false));
        armed = true;

        await Assert.ThrowsAsync<InjectedJournalFailure>(() => store.CompactAsync(
            new JournalCompaction("player/a", 1, "player.v2", 2, new byte[] { 20 }, 1)));

        Assert.Equal(new byte[] { 10 }, (await store.LoadSnapshotAsync("player/a"))!.Data.ToArray());
        Assert.Single((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    private static JournalInitialization Initialization(int suffix, byte[]? snapshot = null)
        => new(Identity(suffix), "player/a", "player.v1", 1, snapshot ?? Array.Empty<byte>(), Array.Empty<JournalProjectionWrite>(), "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, bool projections = true)
    {
        JournalProjectionWrite[] writes = projections
            ? new[] { new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 9 }) }
            : Array.Empty<JournalProjectionWrite>();
        return new JournalCommit(
            Identity(suffix),
            new[] { new JournalStreamMutation("player/a", 0, new[] { new JournalEvent("state.changed", 1, new byte[] { 7 }) }) },
            writes,
            "result.v1",
            1,
            new byte[] { 41 });
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new byte[] { checked((byte)suffix) });

    private static async Task AssertCorrupt(Func<Task> action)
    {
        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(action);
        Assert.Equal(JournalStoreFailureKind.CorruptData, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, exception.Certainty);
    }

    public void Dispose() => database.Dispose();

    private sealed class InjectedJournalFailure : Exception;
}
