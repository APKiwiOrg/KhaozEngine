using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[CollectionDefinition("SQL Server mutation journal", DisableParallelization = true)]
public sealed class SqlServerMutationJournalCollection;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerMutationJournalStoreTests : IDisposable
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING");
    private static string DedicatedConnectionString =>
        SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(ConnectionString);
    private readonly List<SqlServerJournalPrefixStore> ownedStores = new();

    private MutationJournalStoreHarness CreateStore(TimeSpan? minimumRetryHorizon = null)
    {
        SqlServerJournalPrefixStore store = CreatePrefixedStore(minimumRetryHorizon: minimumRetryHorizon);
        var clock = (SqlServerJournalManualTimeProvider)store.TimeProvider;
        return new MutationJournalStoreHarness(
            store,
            store.Maintenance,
            clock.GetUtcNow,
            clock.Advance,
            operationId => SqlServerJournalTestDatabase.CorruptResultChecksumAsync(
                DedicatedConnectionString,
                store.PhysicalOperationId(operationId)),
            duration => SqlServerJournalTestDatabase.AgeOperationsAsync(
                DedicatedConnectionString,
                store.OwnedOperationIds,
                duration));
    }

    private SqlServerJournalPrefixStore CreatePrefixedStore(
        SqlServerJournalTestHook? hook = null,
        TimeSpan? minimumRetryHorizon = null)
    {
        string connectionString = DedicatedConnectionString;
        string prefix = $"journal-test/{Guid.NewGuid():N}/";
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(connectionString)
        {
            MinimumRetryHorizon = minimumRetryHorizon ?? TimeSpan.FromHours(24),
            TimeProvider = clock,
        }, hook);
        var store = new SqlServerJournalPrefixStore(inner, prefix, clock);
        ownedStores.Add(store);
        return store;
    }

    [Fact]
    public void Schema_script_uses_binary_identity_columns_and_does_not_link_events_to_operations()
    {
        string sql = SqlServerMutationJournalStore.SchemaSqlForTest;

        Assert.Contains("Latin1_General_100_BIN2", sql, StringComparison.Ordinal);
        Assert.Contains("operation_id uniqueidentifier", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retention_started_at_utc datetimeoffset(7) NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trg_journal_operation_delete_guard", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#khaoz_journal_operation_delete_guard", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fk_journal_event_operation", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Server=tcp:production.invalid;Initial Catalog=grimhollow-db;Integrated Security=true;")]
    [InlineData("Server=tcp:production.invalid;Database=journal-test;Integrated Security=true;")]
    [InlineData("Server=tcp:production.invalid;Database=shared-journal-test;Integrated Security=true;")]
    public void Live_fixture_rejects_database_without_dedicated_journal_test_marker_before_io(string connectionString)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(connectionString));

        Assert.Contains("-journal-test-", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_fixture_accepts_database_with_dedicated_journal_test_marker()
    {
        const string connectionString =
            "Server=tcp:unreachable.invalid;Database=khaozengine-journal-test-20260906;Integrated Security=true;";

        Assert.Equal(connectionString, SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(connectionString));
    }

    [SqlServerFact]
    public void Auto_create_is_idempotent_and_validate_only_accepts_version_two()
    {
        _ = new SqlServerMutationJournalStore(DedicatedConnectionString);
        _ = new SqlServerMutationJournalStore(DedicatedConnectionString);
        _ = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(DedicatedConnectionString)
        {
            SchemaMode = SqlServerJournalSchemaMode.ValidateOnly,
        });
    }

    [SqlServerFact]
    public async Task Stream_and_section_identity_is_ordinal_and_case_sensitive()
    {
        SqlServerJournalPrefixStore store = CreatePrefixedStore();
        await store.InitializeAsync(Initialization(1, "player/A", Projection("player/A", "Bag", 1)));
        await store.InitializeAsync(Initialization(2, "player/a", Projection("player/a", "bag", 2)));

        Assert.Equal(new byte[] { 1 }, Assert.Single((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/A"))).Sections).Data.ToArray());
        Assert.Equal(new byte[] { 2 }, Assert.Single((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections).Data.ToArray());
    }

    [SqlServerFact]
    public async Task Separate_connections_linearize_competing_expected_versions()
    {
        SqlServerJournalPrefixStore first = CreatePrefixedStore();
        SqlServerJournalPrefixStore second = CreatePrefixedStoreWithPrefix(first.Prefix);
        await first.InitializeAsync(Initialization(1, "player/a"));

        JournalCommitResult[] results = await Task.WhenAll(
            first.CommitAsync(Commit(2, "player/a", 0, 2)),
            second.CommitAsync(Commit(3, "player/a", 0, 3)));

        Assert.Single(results, value => value.Status == JournalCommitStatus.Applied);
        Assert.Single(results, value => value.Status == JournalCommitStatus.VersionConflict);
        Assert.Single((await first.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    [SqlServerFact]
    public async Task Competing_same_operation_id_returns_apply_and_exact_replay()
    {
        SqlServerJournalPrefixStore first = CreatePrefixedStore();
        SqlServerJournalPrefixStore second = CreatePrefixedStoreWithPrefix(first.Prefix);
        await first.InitializeAsync(Initialization(1, "player/a"));
        JournalCommit commit = Commit(2, "player/a", 0, 7);

        JournalCommitResult[] results = await Task.WhenAll(first.CommitAsync(commit), second.CommitAsync(commit));

        Assert.Single(results, value => value.Status == JournalCommitStatus.Applied);
        JournalCommitResult replay = Assert.Single(results, value => value.Status == JournalCommitStatus.Replayed);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Single((await first.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    [SqlServerFact]
    public async Task Prefix_cleanup_preserves_rows_owned_by_another_fixture()
    {
        SqlServerJournalPrefixStore removed = CreatePrefixedStore();
        SqlServerJournalPrefixStore survivor = CreatePrefixedStore();
        await removed.InitializeAsync(Initialization(1, "player/a"));
        await survivor.InitializeAsync(Initialization(1, "player/a"));

        await SqlServerJournalTestDatabase.CleanupAsync(DedicatedConnectionString, removed.Prefix, removed.OwnedOperationIds);

        Assert.Null(await removed.LoadSnapshotAsync("player/a"));
        Assert.NotNull(await survivor.LoadSnapshotAsync("player/a"));
    }

    [SqlServerFact] public Task Initialization_creates_version_zero_exactly_once() => Conformance().Initialization_creates_version_zero_exactly_once();
    [SqlServerFact] public Task Identical_initialization_replays_original_receipt_and_result() => Conformance().Identical_initialization_replays_original_receipt_and_result();
    [SqlServerFact] public Task Different_operation_on_existing_stream_returns_existing_stream() => Conformance().Different_operation_on_existing_stream_returns_existing_stream();
    [SqlServerFact] public Task Reused_operation_id_with_different_intent_returns_operation_conflict() => Conformance().Reused_operation_id_with_different_intent_returns_operation_conflict();
    [SqlServerFact] public Task Commit_appends_contiguous_versions_and_replaces_projection_sections() => Conformance().Commit_appends_contiguous_versions_and_replaces_projection_sections();
    [SqlServerFact] public Task Multi_stream_commit_is_all_or_nothing() => Conformance().Multi_stream_commit_is_all_or_nothing();
    [SqlServerFact] public Task Version_conflict_and_missing_stream_write_nothing() => Conformance().Version_conflict_and_missing_stream_write_nothing();
    [SqlServerFact] public Task Eventless_constraints_do_not_advance_heads_and_cannot_write_projections() => Conformance().Eventless_constraints_do_not_advance_heads_and_cannot_write_projections();
    [SqlServerFact] public Task Matching_identity_replays_original_execution_and_result() => Conformance().Matching_identity_replays_original_execution_and_result();
    [SqlServerFact] public Task Every_replay_path_verifies_the_stored_result_checksum() => Conformance().Every_replay_path_verifies_the_stored_result_checksum();
    [SqlServerFact] public Task Snapshot_and_ordered_pages_reconstruct_stream_and_pruned_prefix_requires_snapshot() => Conformance().Snapshot_and_ordered_pages_reconstruct_stream_and_pruned_prefix_requires_snapshot();
    [SqlServerFact] public Task Event_page_continuation_stays_bound_to_first_page_head() => Conformance().Event_page_continuation_stays_bound_to_first_page_head();
    [SqlServerFact] public Task Projection_cursors_bind_epoch_stream_and_section_versions() => Conformance().Projection_cursors_bind_epoch_stream_and_section_versions();
    [SqlServerFact] public Task Stale_epoch_cursor_requests_full_reset() => Conformance().Stale_epoch_cursor_requests_full_reset();
    [SqlServerFact] public Task Malformed_projection_cursor_requests_full_reset_and_replacement() => Conformance().Malformed_projection_cursor_requests_full_reset_and_replacement();
    [SqlServerFact] public Task Future_projection_cursor_requests_full_reset_and_replacement() => Conformance().Future_projection_cursor_requests_full_reset_and_replacement();
    [SqlServerFact] public Task Compaction_preserves_recoverable_snapshot_and_retained_tail() => Conformance().Compaction_preserves_recoverable_snapshot_and_retained_tail();
    [SqlServerFact] public Task Snapshot_only_compaction_replaces_snapshot_without_pruning_events() => Conformance().Snapshot_only_compaction_replaces_snapshot_without_pruning_events();
    [SqlServerFact] public Task Purge_enforces_retry_horizon_and_deterministic_bounded_order() => Conformance().Purge_enforces_retry_horizon_and_deterministic_bounded_order();
    [SqlServerFact] public Task Purge_removes_replay_children_before_parent_and_preserves_events() => Conformance().Purge_removes_replay_children_before_parent_and_preserves_events();
    [SqlServerFact] public Task Cancellation_before_work_leaves_no_rows() => Conformance().Cancellation_before_work_leaves_no_rows();
    [SqlServerFact] public Task Concurrent_commits_against_one_head_are_linearizable() => Conformance().Concurrent_commits_against_one_head_are_linearizable();

    private BoundConformance Conformance() => new(CreateStore);

    private SqlServerJournalPrefixStore CreatePrefixedStoreWithPrefix(string prefix)
    {
        string connectionString = DedicatedConnectionString;
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(connectionString)
        {
            TimeProvider = clock,
        });
        var store = new SqlServerJournalPrefixStore(inner, prefix, clock);
        ownedStores.Add(store);
        return store;
    }

    private static JournalOperationIdentity Identity(int suffix, byte[]? intent = null)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", intent ?? new byte[] { checked((byte)suffix) });

    private static JournalInitialization Initialization(int suffix, string streamKey, params JournalProjectionWrite[] projections)
        => new(Identity(suffix), streamKey, "player.v1", 1, Array.Empty<byte>(), projections, "result.v1", 1, new byte[] { 1 });

    private static JournalProjectionWrite Projection(string streamKey, string sectionName, byte value)
        => new(streamKey, sectionName, "section.v1", 1, new byte[] { value });

    private static JournalCommit Commit(int suffix, string streamKey, long expectedVersion, byte eventValue)
        => new(
            Identity(suffix),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, new[] { new JournalEvent("state.changed", 1, new byte[] { eventValue }) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { eventValue });

    private sealed class BoundConformance(Func<TimeSpan?, MutationJournalStoreHarness> createStore)
        : MutationJournalStoreConformance
    {
        protected override MutationJournalStoreHarness CreateStore(TimeSpan? minimumRetryHorizon = null)
            => createStore(minimumRetryHorizon);
    }

    public void Dispose()
    {
        if (ownedStores.Count == 0) return;
        foreach (SqlServerJournalPrefixStore store in ownedStores)
            SqlServerJournalTestDatabase.CleanupAsync(DedicatedConnectionString, store.Prefix, store.OwnedOperationIds).GetAwaiter().GetResult();
    }
}
