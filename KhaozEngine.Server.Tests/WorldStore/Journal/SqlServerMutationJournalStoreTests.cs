using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[CollectionDefinition("SQL Server mutation journal", DisableParallelization = true)]
public sealed class SqlServerMutationJournalCollection;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerMutationJournalStoreTests : IDisposable
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING");
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
                ConnectionString!,
                store.PhysicalOperationId(operationId)));
    }

    private SqlServerJournalPrefixStore CreatePrefixedStore(
        SqlServerJournalTestHook? hook = null,
        TimeSpan? minimumRetryHorizon = null)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SQL Server journal tests require KE_SQLSERVER_TEST_CONNSTRING.");

        string prefix = $"journal-test/{Guid.NewGuid():N}/";
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(ConnectionString)
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
        Assert.DoesNotContain("fk_journal_event_operation", sql, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public void Auto_create_is_idempotent_and_validate_only_accepts_version_one()
    {
        _ = new SqlServerMutationJournalStore(ConnectionString!);
        _ = new SqlServerMutationJournalStore(ConnectionString!);
        _ = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(ConnectionString!)
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

        await SqlServerJournalTestDatabase.CleanupAsync(ConnectionString!, removed.Prefix, removed.OwnedOperationIds);

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
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SQL Server journal tests require KE_SQLSERVER_TEST_CONNSTRING.");
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(ConnectionString)
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
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        foreach (SqlServerJournalPrefixStore store in ownedStores)
            SqlServerJournalTestDatabase.CleanupAsync(ConnectionString, store.Prefix, store.OwnedOperationIds).GetAwaiter().GetResult();
    }
}

internal sealed class SqlServerJournalManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    public override DateTimeOffset GetUtcNow() => current;
    internal void Advance(TimeSpan amount) => current += amount;
}

internal sealed class SqlServerJournalPrefixStore : IMutationJournalStore
{
    private readonly SqlServerMutationJournalStore inner;
    private readonly byte[] operationMask;
    private readonly HashSet<Guid> ownedOperationIds = new();

    internal SqlServerJournalPrefixStore(SqlServerMutationJournalStore inner, string prefix, TimeProvider timeProvider)
    {
        this.inner = inner;
        Prefix = prefix;
        TimeProvider = timeProvider;
        operationMask = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(prefix))[..16];
        Array.Clear(operationMask, 10, 6);
    }

    internal string Prefix { get; }
    internal IMutationJournalMaintenance Maintenance => inner;
    internal TimeProvider TimeProvider { get; }
    internal IReadOnlyCollection<Guid> OwnedOperationIds
    {
        get
        {
            lock (ownedOperationIds) return ownedOperationIds.ToArray();
        }
    }

    internal Guid PhysicalOperationId(Guid value)
    {
        Guid mapped = TransformOperationId(value);
        lock (ownedOperationIds) ownedOperationIds.Add(mapped);
        return mapped;
    }

    public async Task<JournalOperationResolution> ResolveOperationAsync(JournalOperationIdentity identity, CancellationToken cancellationToken = default)
    {
        JournalOperationResolution result = await inner.ResolveOperationAsync(Map(identity), cancellationToken);
        return new JournalOperationResolution(result.Status, result.Receipt is null ? null : Unmap(result.Receipt));
    }

    public async Task<JournalInitializeResult> InitializeAsync(JournalInitialization initialization, CancellationToken cancellationToken = default)
    {
        var mapped = new JournalInitialization(
            Map(initialization.Identity),
            Map(initialization.AbsentStreamKey),
            initialization.SnapshotSchema,
            initialization.SnapshotSchemaVersion,
            initialization.SnapshotData.ToArray(),
            initialization.ProjectionWrites.Select(Map).ToArray(),
            initialization.ResultSchema,
            initialization.ResultSchemaVersion,
            initialization.ResultData.ToArray());
        JournalInitializeResult result = await inner.InitializeAsync(mapped, cancellationToken);
        return new JournalInitializeResult(result.Status, result.Receipt is null ? null : Unmap(result.Receipt));
    }

    public async Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default)
    {
        var mapped = new JournalCommit(
            Map(commit.Identity),
            commit.StreamMutations.Select(value => new JournalStreamMutation(Map(value.StreamKey), value.ExpectedVersion, value.Events)).ToArray(),
            commit.ProjectionWrites.Select(Map).ToArray(),
            commit.ResultSchema,
            commit.ResultSchemaVersion,
            commit.ResultData.ToArray());
        JournalCommitResult result = await inner.CommitAsync(mapped, cancellationToken);
        return new JournalCommitResult(result.Status, result.Receipt is null ? null : Unmap(result.Receipt));
    }

    public async Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default)
    {
        JournalSnapshot? value = await inner.LoadSnapshotAsync(Map(streamKey), cancellationToken);
        return value is null
            ? null
            : new JournalSnapshot(Unmap(value.StreamKey), value.ThroughVersion, value.SnapshotSchema, value.SnapshotSchemaVersion,
                value.Data.ToArray(), value.DataChecksum.ToArray(), value.CreatedAtUtc);
    }

    public async Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default)
    {
        JournalEventPage page = await inner.ReadEventsAsync(
            new JournalEventRead(Map(read.StreamKey), read.AfterVersion, read.ThroughVersion, read.MaxEvents, read.MaxBytes),
            cancellationToken);
        JournalStoredEvent[] events = page.Events.Select(value => new JournalStoredEvent(
            Unmap(value.StreamKey), value.StreamVersion, TransformOperationId(value.OperationId), value.OperationOrdinal,
            value.EventType, value.EventSchemaVersion, value.Payload.ToArray(), value.PayloadChecksum.ToArray(), value.CommittedAtUtc)).ToArray();
        return new JournalEventPage(page.Status, Unmap(page.StreamKey), page.ThroughVersion, events, page.ReachedThroughVersion);
    }

    public async Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default)
    {
        JournalProjectionRead read = await inner.ReadProjectionsAsync(
            new JournalProjectionQuery(Map(query.StreamKey), MapCursor(query.Cursor)),
            cancellationToken);
        JournalProjectionSection[] sections = read.Sections.Select(value => new JournalProjectionSection(
            Unmap(value.StreamKey), value.SectionName, value.SourceVersion, value.ProjectionSchema,
            value.ProjectionSchemaVersion, value.Data.ToArray(), value.DataChecksum.ToArray(), value.UpdatedAtUtc)).ToArray();
        return new JournalProjectionRead(read.Status, Unmap(read.StreamKey), read.HeadVersion, sections, UnmapCursor(read.Cursor));
    }

    public Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default)
        => inner.CompactAsync(new JournalCompaction(
            Map(compaction.StreamKey),
            compaction.ThroughVersion,
            compaction.SnapshotSchema,
            compaction.SnapshotSchemaVersion,
            compaction.SnapshotData.ToArray(),
            compaction.PruneThroughVersion), cancellationToken);

    private JournalOperationIdentity Map(JournalOperationIdentity identity)
        => new(PhysicalOperationId(identity.OperationId), identity.AuthenticatedScope, identity.ActionKind, identity.NormalizedIntent.ToArray());

    private JournalProjectionWrite Map(JournalProjectionWrite value)
        => new(Map(value.StreamKey), value.SectionName, value.ProjectionSchema, value.ProjectionSchemaVersion, value.Data.ToArray());

    private string Map(string streamKey) => Prefix + streamKey;
    private string Unmap(string streamKey) => streamKey.StartsWith(Prefix, StringComparison.Ordinal) ? streamKey[Prefix.Length..] : streamKey;

    private JournalCommitReceipt Unmap(JournalCommitReceipt receipt)
        => new(
            TransformOperationId(receipt.OperationId),
            receipt.CommittedAtUtc,
            receipt.Streams.Select(value => new JournalStreamVersionRange(Unmap(value.StreamKey), value.BeforeVersion, value.AfterVersion, value.EventCount)).ToArray(),
            receipt.ResultSchema,
            receipt.ResultSchemaVersion,
            receipt.ResultData.ToArray(),
            receipt.ResultChecksum.ToArray(),
            receipt.IsReplay);

    private Guid TransformOperationId(Guid value)
    {
        byte[] bytes = value.ToByteArray();
        for (int i = 0; i < bytes.Length; i++) bytes[i] ^= operationMask[i];
        return new Guid(bytes);
    }

    private string? MapCursor(string? cursor)
    {
        if (!JournalProjectionCursor.TryDecode(cursor, out Guid epoch, out string streamKey, out long head)) return cursor;
        return JournalProjectionCursor.Encode(epoch, Map(streamKey), head);
    }

    private string? UnmapCursor(string? cursor)
    {
        if (!JournalProjectionCursor.TryDecode(cursor, out Guid epoch, out string streamKey, out long head)) return cursor;
        return JournalProjectionCursor.Encode(epoch, Unmap(streamKey), head);
    }
}

internal static class SqlServerJournalTestDatabase
{
    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<bool> IndexExistsAsync(string connectionString, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = @name;";
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = indexName;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) != 0;
    }

    internal static async Task SetHeadAsync(string connectionString, string streamKey, long head)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.journal_stream SET current_version = @head WHERE stream_key = @stream;";
        command.Parameters.Add("@head", SqlDbType.BigInt).Value = head;
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 256).Value = streamKey;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal static async Task CorruptResultChecksumAsync(string connectionString, Guid operationId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.journal_operation SET result_sha256 = @checksum WHERE operation_id = @id;";
        command.Parameters.Add("@checksum", SqlDbType.Binary, 32).Value = new byte[32];
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync();
    }

    internal static Task CorruptSnapshotChecksumAsync(string connectionString, string streamKey)
        => CorruptStreamChecksumAsync(connectionString, "journal_snapshot", "data_sha256", streamKey);

    internal static Task CorruptEventChecksumAsync(string connectionString, string streamKey)
        => CorruptStreamChecksumAsync(connectionString, "journal_event", "payload_sha256", streamKey);

    internal static Task CorruptProjectionChecksumAsync(string connectionString, string streamKey)
        => CorruptStreamChecksumAsync(connectionString, "journal_projection", "data_sha256", streamKey);

    private static async Task CorruptStreamChecksumAsync(
        string connectionString,
        string table,
        string checksumColumn,
        string streamKey)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"UPDATE dbo.{table} SET {checksumColumn} = @checksum WHERE stream_key = @stream;";
        command.Parameters.Add("@checksum", SqlDbType.Binary, 32).Value = new byte[32];
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 256).Value = streamKey;
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task CleanupAsync(string connectionString, string prefix, IReadOnlyCollection<Guid> operationIds)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            string escapedPrefix = EscapeLike(prefix) + "%";
            foreach (string table in new[] { "journal_projection", "journal_snapshot", "journal_event", "journal_operation_stream" })
            {
                await using SqlCommand child = connection.CreateCommand();
                child.Transaction = transaction;
                child.CommandText = $"DELETE FROM dbo.{table} WHERE stream_key LIKE @prefix ESCAPE '\\';";
                child.Parameters.Add("@prefix", SqlDbType.NVarChar, 256).Value = escapedPrefix;
                await child.ExecuteNonQueryAsync();
            }
            await using (SqlCommand streams = connection.CreateCommand())
            {
                streams.Transaction = transaction;
                streams.CommandText = "DELETE FROM dbo.journal_stream WHERE stream_key LIKE @prefix ESCAPE '\\';";
                streams.Parameters.Add("@prefix", SqlDbType.NVarChar, 256).Value = escapedPrefix;
                await streams.ExecuteNonQueryAsync();
            }
            foreach (Guid operationId in operationIds)
            {
                await using SqlCommand operation = connection.CreateCommand();
                operation.Transaction = transaction;
                operation.CommandText = "DELETE FROM dbo.journal_operation_stream WHERE operation_id = @id; DELETE FROM dbo.journal_operation WHERE operation_id = @id;";
                operation.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
                await operation.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
