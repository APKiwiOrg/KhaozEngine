using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

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
    internal IMutationJournalAgeMaintenance AgeMaintenance => inner;
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
    private const string DedicatedDatabaseMarker = "-journal-test-";

    internal static string RequireDedicatedTestDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SQL Server journal tests require KE_SQLSERVER_TEST_CONNSTRING.");
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.Contains(DedicatedDatabaseMarker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SQL Server journal live tests require Initial Catalog to contain '{DedicatedDatabaseMarker}'.");
        return connectionString;
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
            await using (SqlCommand guard = connection.CreateCommand())
            {
                guard.Transaction = transaction;
                guard.CommandText = "CREATE TABLE #khaoz_journal_operation_delete_guard (guard bit NOT NULL);";
                await guard.ExecuteNonQueryAsync();
            }
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
            await using (SqlCommand guard = connection.CreateCommand())
            {
                guard.Transaction = transaction;
                guard.CommandText = "DROP TABLE #khaoz_journal_operation_delete_guard;";
                await guard.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    internal static async Task AgeOperationsAsync(
        string connectionString,
        IReadOnlyCollection<Guid> operationIds,
        TimeSpan duration)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (Guid operationId in operationIds)
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dbo.journal_operation
                SET retention_started_at_utc = DATEADD_BIG(millisecond, @elapsed, retention_started_at_utc)
                WHERE operation_id = @id;
                """;
            command.Parameters.Add("@elapsed", SqlDbType.BigInt).Value = -checked((long)duration.TotalMilliseconds);
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = operationId;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
