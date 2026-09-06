using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerMutationJournalFailureTests : IDisposable
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING");
    private readonly List<SqlServerJournalPrefixStore> ownedStores = new();

    [Theory]
    [InlineData(1205, JournalStoreFailureKind.Deadlock)]
    [InlineData(1222, JournalStoreFailureKind.Timeout)]
    [InlineData(-2, JournalStoreFailureKind.Timeout)]
    [InlineData(2627, JournalStoreFailureKind.ConstraintViolation)]
    [InlineData(2601, JournalStoreFailureKind.ConstraintViolation)]
    public void Provider_error_numbers_map_to_the_shared_failure_contract(
        int errorNumber,
        JournalStoreFailureKind expectedKind)
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapFailureForTest(
            errorNumber,
            transactionStarted: true,
            commitStarted: false,
            rollbackConfirmed: true,
            new[] { "player/a" });

        Assert.Equal(expectedKind, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, mapped.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, mapped.Scope);
        Assert.Equal(new[] { "player/a" }, mapped.StreamKeys);
    }

    [Fact]
    public void Connection_loss_during_commit_is_unknown_when_rollback_is_unproved()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapFailureForTest(
            10054,
            transactionStarted: true,
            commitStarted: true,
            rollbackConfirmed: false,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, mapped.Certainty);
    }

    [Fact]
    public void Confirmed_rollback_preserves_transport_failure_kind_before_commit()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapFailureForTest(
            10054,
            transactionStarted: true,
            commitStarted: false,
            rollbackConfirmed: true,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.Unavailable, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, mapped.Certainty);
    }

    [Fact]
    public void Unconfirmed_transport_loss_after_transaction_start_is_unknown_before_commit()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapFailureForTest(
            10054,
            transactionStarted: true,
            commitStarted: false,
            rollbackConfirmed: false,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, mapped.Certainty);
    }

    [Fact]
    public void Cancellation_after_transaction_start_is_definite_when_rollback_succeeds()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapCancellationForTest(
            transactionStarted: true,
            commitStarted: false,
            rollbackConfirmed: true,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.Cancelled, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, mapped.Certainty);
    }

    [Fact]
    public void Cancellation_while_committing_is_unknown_when_rollback_is_unproved()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapCancellationForTest(
            transactionStarted: true,
            commitStarted: true,
            rollbackConfirmed: false,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, mapped.Certainty);
    }

    [Fact]
    public void Non_provider_transport_failure_while_committing_is_unknown()
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapTransportFailureForTest(
            new IOException("connection closed"),
            commitStarted: true,
            rollbackConfirmed: false,
            new[] { "player/a" });

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, mapped.Certainty);
    }

    [Fact]
    public void Stream_locks_are_sorted_with_ordinal_binary_semantics()
    {
        string[] ordered = SqlServerMutationJournalStore.OrderStreamKeysForTest(
            new[] { "player/a", "player/A", "player:10", "player:2" });

        Assert.Equal(new[] { "player/A", "player/a", "player:10", "player:2" }, ordered);
    }

    [Theory]
    [InlineData(-1, JournalStoreFailureKind.Timeout)]
    [InlineData(-2, JournalStoreFailureKind.Cancelled)]
    [InlineData(-3, JournalStoreFailureKind.Deadlock)]
    [InlineData(-999, JournalStoreFailureKind.Unavailable)]
    public void Application_lock_failures_are_classified_explicitly(int returnCode, JournalStoreFailureKind expectedKind)
    {
        JournalStoreException mapped = SqlServerMutationJournalStore.MapApplicationLockFailureForTest(returnCode);

        Assert.Equal(expectedKind, mapped.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, mapped.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, mapped.Scope);
    }

    [SqlServerFact]
    public Task Failure_after_event_writes_rolls_back_all_mutation_rows()
        => InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase.AfterEventWrites);

    [SqlServerFact]
    public Task Failure_after_projection_writes_rolls_back_all_mutation_rows()
        => InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase.AfterProjectionWrites);

    [SqlServerFact]
    public async Task Failure_after_commit_maps_to_unknown_outcome_and_same_id_replays()
    {
        bool armed = false;
        var hook = new SqlServerJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.AfterCommitBeforeResponse) throw new InjectedJournalFailure();
        });
        SqlServerJournalPrefixStore store = CreateStore(hook);
        await store.InitializeAsync(Initialization(1));
        JournalCommit commit = Commit(2);
        armed = true;

        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(commit));
        armed = false;
        JournalCommitResult replay = await store.CommitAsync(commit);

        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, exception.Certainty);
        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.Single((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    [SqlServerFact]
    public Task Initialize_duplicate_operation_key_returns_replay()
        => InitializeOperationInsertCollisionResolvesAuthoritativeRow(mismatch: false, JournalInitializeStatus.Replayed);

    [SqlServerFact]
    public Task Initialize_duplicate_operation_key_returns_conflict()
        => InitializeOperationInsertCollisionResolvesAuthoritativeRow(mismatch: true, JournalInitializeStatus.OperationConflict);

    [SqlServerFact]
    public Task Commit_duplicate_operation_key_returns_replay()
        => CommitOperationInsertCollisionResolvesAuthoritativeRow(mismatch: false, JournalCommitStatus.Replayed);

    [SqlServerFact]
    public Task Commit_duplicate_operation_key_returns_conflict()
        => CommitOperationInsertCollisionResolvesAuthoritativeRow(mismatch: true, JournalCommitStatus.OperationConflict);

    [SqlServerFact]
    public async Task Duplicate_event_key_rereads_and_replays_operation_winner()
    {
        SqlServerJournalPrefixStore seed = CreateStore();
        await seed.InitializeAsync(Initialization(40));
        await seed.CommitAsync(Commit(41, projections: false));
        await seed.InitializeAsync(new JournalInitialization(
            Identity(42), "player/b", "player.v1", 1, Array.Empty<byte>(),
            Array.Empty<JournalProjectionWrite>(), "result.v1", 1, new byte[] { 42 }));
        await SqlServerJournalTestDatabase.SetHeadAsync(ConnectionString!, seed.Prefix + "player/a", 0);
        SqlServerJournalPrefixStore racing = CreateStore(new SqlServerJournalTestHook(_ => { }, suppressedOperationLookups: 1), seed.Prefix);

        JournalCommitResult result = await racing.CommitAsync(new JournalCommit(
            Identity(42),
            new[] { new JournalStreamMutation("player/a", 0, new[] { new JournalEvent("state.changed", 1, new byte[] { 8 }) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { 8 }));

        Assert.Equal(JournalCommitStatus.Replayed, result.Status);
        Assert.Equal(new byte[] { 42 }, result.Receipt!.ResultData.ToArray());
    }

    [SqlServerFact]
    public async Task Duplicate_event_key_without_operation_winner_surfaces_constraint_failure()
    {
        SqlServerJournalPrefixStore store = CreateStore();
        await store.InitializeAsync(Initialization(50));
        await store.CommitAsync(Commit(51, projections: false));
        await SqlServerJournalTestDatabase.SetHeadAsync(ConnectionString!, store.Prefix + "player/a", 0);
        SqlServerJournalPrefixStore racing = CreateStore(new SqlServerJournalTestHook(_ => { }, suppressedOperationLookups: 1), store.Prefix);
        JournalCommit attempt = new(
            Identity(52),
            new[] { new JournalStreamMutation("player/a", 0, new[] { new JournalEvent("state.changed", 1, new byte[] { 8 }) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { 8 });

        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(() => racing.CommitAsync(attempt));

        Assert.Equal(JournalStoreFailureKind.ConstraintViolation, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, exception.Certainty);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await racing.ResolveOperationAsync(Identity(52))).Status);
    }

    [SqlServerFact]
    public async Task Commit_invokes_shared_mutation_boundaries_in_order()
    {
        var phases = new List<JournalTestHookPhase>();
        SqlServerJournalPrefixStore store = CreateStore(new SqlServerJournalTestHook(phases.Add));
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

    [SqlServerFact]
    public async Task Malformed_stored_checksums_map_to_committed_data_corruption()
    {
        SqlServerJournalPrefixStore snapshotStore = CreateStore();
        await snapshotStore.InitializeAsync(Initialization(1, new byte[] { 3 }));
        await SqlServerJournalTestDatabase.CorruptSnapshotChecksumAsync(ConnectionString!, snapshotStore.Prefix + "player/a");
        await AssertCorrupt(() => snapshotStore.LoadSnapshotAsync("player/a"));

        SqlServerJournalPrefixStore eventStore = CreateStore();
        await eventStore.InitializeAsync(Initialization(2));
        await eventStore.CommitAsync(Commit(3));
        await SqlServerJournalTestDatabase.CorruptEventChecksumAsync(ConnectionString!, eventStore.Prefix + "player/a");
        await AssertCorrupt(() => eventStore.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024)));

        SqlServerJournalPrefixStore projectionStore = CreateStore();
        await projectionStore.InitializeAsync(Initialization(4));
        await projectionStore.CommitAsync(Commit(5));
        await SqlServerJournalTestDatabase.CorruptProjectionChecksumAsync(ConnectionString!, projectionStore.Prefix + "player/a");
        await AssertCorrupt(() => projectionStore.ReadProjectionsAsync(new JournalProjectionQuery("player/a")));
    }

    [SqlServerFact]
    public Task Failure_after_snapshot_write_preserves_prior_snapshot_and_tail()
        => CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase.SnapshotWrittenBeforeVerification);

    [SqlServerFact]
    public Task Failure_before_event_prune_preserves_prior_snapshot_and_tail()
        => CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase.SnapshotVerifiedBeforePrune);

    [SqlServerFact]
    public async Task Validate_only_and_auto_create_reject_malformed_version_one_without_ddl_repair()
    {
        await SqlServerJournalTestDatabase.ExecuteAsync(ConnectionString!, "DROP INDEX ix_journal_projection_version ON dbo.journal_projection;");
        try
        {
            JournalStoreException validateOnly = Assert.Throws<JournalStoreException>(() => new SqlServerMutationJournalStore(
                new SqlServerMutationJournalStoreOptions(ConnectionString!) { SchemaMode = SqlServerJournalSchemaMode.ValidateOnly }));
            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, validateOnly.Kind);
            Assert.Contains("sqlserver-journal-v1-create", validateOnly.Message, StringComparison.Ordinal);
            Assert.False(await SqlServerJournalTestDatabase.IndexExistsAsync(ConnectionString!, "ix_journal_projection_version"));

            JournalStoreException autoCreate = Assert.Throws<JournalStoreException>(() => new SqlServerMutationJournalStore(ConnectionString!));
            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, autoCreate.Kind);
            Assert.False(await SqlServerJournalTestDatabase.IndexExistsAsync(ConnectionString!, "ix_journal_projection_version"));
        }
        finally
        {
            await SqlServerJournalTestDatabase.ExecuteAsync(
                ConnectionString!,
                "CREATE INDEX ix_journal_projection_version ON dbo.journal_projection(stream_key, source_version);");
        }
    }

    private async Task InjectedStatementBoundaryFailureRollsBackAllMutationRows(JournalTestHookPhase failurePhase)
    {
        bool armed = false;
        var hook = new SqlServerJournalTestHook(phase =>
        {
            if (armed && phase == failurePhase) throw new InjectedJournalFailure();
        });
        SqlServerJournalPrefixStore store = CreateStore(hook);
        await store.InitializeAsync(Initialization(1));
        JournalCommit commit = Commit(2);
        armed = true;

        await Assert.ThrowsAsync<InjectedJournalFailure>(() => store.CommitAsync(commit));

        Assert.Empty((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        Assert.Empty((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(commit.Identity)).Status);
    }

    private async Task InitializeOperationInsertCollisionResolvesAuthoritativeRow(bool mismatch, JournalInitializeStatus expectedStatus)
    {
        JournalInitialization committed = Initialization(20);
        SqlServerJournalPrefixStore seed = CreateStore();
        await seed.InitializeAsync(committed);
        SqlServerJournalPrefixStore racing = CreateStore(new SqlServerJournalTestHook(_ => { }, suppressedOperationLookups: 1), seed.Prefix);
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
        Assert.Equal(JournalProjectionReadStatus.NotFound, (await racing.ReadProjectionsAsync(new JournalProjectionQuery("player/b"))).Status);
        if (!mismatch) Assert.Equal(committed.ResultData.ToArray(), result.Receipt!.ResultData.ToArray());
    }

    private async Task CommitOperationInsertCollisionResolvesAuthoritativeRow(bool mismatch, JournalCommitStatus expectedStatus)
    {
        SqlServerJournalPrefixStore seed = CreateStore();
        await seed.InitializeAsync(Initialization(30));
        await seed.InitializeAsync(new JournalInitialization(
            Identity(31), "player/b", "player.v1", 1, Array.Empty<byte>(),
            Array.Empty<JournalProjectionWrite>(), "result.v1", 1, new byte[] { 1 }));
        await seed.CommitAsync(Commit(32));
        SqlServerJournalPrefixStore racing = CreateStore(new SqlServerJournalTestHook(_ => { }, suppressedOperationLookups: 1), seed.Prefix);
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

    private async Task CompactionFailureBoundaryPreservesPriorSnapshotAndTail(JournalTestHookPhase failurePhase)
    {
        bool armed = false;
        var hook = new SqlServerJournalTestHook(phase =>
        {
            if (armed && phase == failurePhase) throw new InjectedJournalFailure();
        });
        SqlServerJournalPrefixStore store = CreateStore(hook);
        await store.InitializeAsync(Initialization(1, new byte[] { 10 }));
        await store.CommitAsync(Commit(2, projections: false));
        armed = true;

        await Assert.ThrowsAsync<InjectedJournalFailure>(() => store.CompactAsync(
            new JournalCompaction("player/a", 1, "player.v2", 2, new byte[] { 20 }, 1)));

        Assert.Equal(new byte[] { 10 }, (await store.LoadSnapshotAsync("player/a"))!.Data.ToArray());
        Assert.Single((await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    private SqlServerJournalPrefixStore CreateStore(SqlServerJournalTestHook? hook = null, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SQL Server journal tests require KE_SQLSERVER_TEST_CONNSTRING.");
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(
            new SqlServerMutationJournalStoreOptions(ConnectionString) { TimeProvider = clock },
            hook);
        var store = new SqlServerJournalPrefixStore(inner, prefix ?? $"journal-test/{Guid.NewGuid():N}/", clock);
        ownedStores.Add(store);
        return store;
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

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        foreach (SqlServerJournalPrefixStore store in ownedStores)
            SqlServerJournalTestDatabase.CleanupAsync(ConnectionString, store.Prefix, store.OwnedOperationIds).GetAwaiter().GetResult();
    }

    private sealed class InjectedJournalFailure : Exception;
}
