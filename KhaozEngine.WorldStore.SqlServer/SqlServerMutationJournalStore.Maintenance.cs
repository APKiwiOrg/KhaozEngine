using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    public async Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        cancellationToken.ThrowIfCancellationRequested();
        compaction.Validate(limits);
        string[] streamKeys = { compaction.StreamKey };
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AcquireMaintenanceLockAsync(transaction, exclusive: true, cancellationToken).ConfigureAwait(false);
            (bool found, long head, _) = await ReadHeadAsync(compaction.StreamKey, transaction, cancellationToken, forUpdate: true).ConfigureAwait(false);
            if (!found)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new JournalCompactionResult(JournalCompactionStatus.NotFound, 0, 0, 0);
            }
            long previousVersion = await ReadSnapshotVersionAsync(compaction.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (head < compaction.ThroughVersion || compaction.ThroughVersion <= previousVersion)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new JournalCompactionResult(JournalCompactionStatus.VersionConflict, previousVersion, previousVersion, 0);
            }
            Invoke(JournalTestHookPhase.AfterHeadValidation);

            DateTimeOffset now = UtcNow();
            await ReplaceSnapshotAsync(compaction, now, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.SnapshotWrittenBeforeVerification);
            await VerifyReplacementSnapshotAsync(compaction, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.SnapshotVerifiedBeforePrune);

            int pruned = 0;
            if (compaction.PruneThroughVersion is long pruneThrough)
            {
                using (SqlCommand prune = CreateCommand(transaction, "DELETE FROM dbo.journal_event WHERE stream_key = @stream AND stream_version <= @through;"))
                {
                    Add(prune, "@stream", compaction.StreamKey);
                    Add(prune, "@through", pruneThrough);
                    pruned = await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqlCommand floor = CreateCommand(transaction, """
                    UPDATE dbo.journal_stream
                    SET retained_floor = CASE WHEN retained_floor < @through THEN @through ELSE retained_floor END,
                        updated_at_utc = @now
                    WHERE stream_key = @stream;
                    """);
                Add(floor, "@through", pruneThrough);
                Add(floor, "@now", Timestamp(now));
                Add(floor, "@stream", compaction.StreamKey);
                await floor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalCompactionResult(JournalCompactionStatus.Compacted, previousVersion, compaction.ThroughVersion, pruned);
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, streamKeys, commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public async Task<JournalOperationPurgeResult> PurgeOperationsAsync(
        JournalOperationPurge purge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purge);
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AcquireMaintenanceLockAsync(transaction, exclusive: true, cancellationToken).ConfigureAwait(false);
            await OpenOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            DateTimeOffset databaseNow = await ReadDatabaseUtcNowAsync(transaction, cancellationToken).ConfigureAwait(false);
            DateTimeOffset safeCutoff = SubtractOrMinimum(databaseNow, minimumRetryHorizon);
            DateTimeOffset effectiveCutoff = purge.CutoffUtc < safeCutoff ? purge.CutoffUtc : safeCutoff;
            using SqlCommand select = CreateCommand(transaction, """
                SELECT TOP (@limit) operation_id, retention_started_at_utc
                FROM dbo.journal_operation WITH (UPDLOCK, HOLDLOCK)
                WHERE committed_at_utc <= @cutoff
                ORDER BY committed_at_utc,
                         CONVERT(char(36), operation_id) COLLATE Latin1_General_100_BIN2;
                """);
            Add(select, "@cutoff", Timestamp(effectiveCutoff));
            Add(select, "@limit", purge.MaxOperations);
            var candidates = new List<(Guid OperationId, DateTimeOffset RetentionStartedAt)>();
            await using (SqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
            }

            int ineligible = 0;
            int deleted = 0;
            foreach ((Guid operationId, DateTimeOffset retentionStartedAt) in candidates)
            {
                if (retentionStartedAt > safeCutoff)
                {
                    ineligible++;
                    continue;
                }
                using (SqlCommand children = CreateCommand(transaction, "DELETE FROM dbo.journal_operation_stream WHERE operation_id = @id;"))
                {
                    Add(children, "@id", operationId);
                    await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqlCommand parent = CreateCommand(transaction, "DELETE FROM dbo.journal_operation WHERE operation_id = @id;");
                Add(parent, "@id", operationId);
                deleted += await parent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using SqlCommand oldestCommand = CreateCommand(transaction, "SELECT MIN(committed_at_utc) FROM dbo.journal_operation;");
            object? oldestRaw = await oldestCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? oldest = oldestRaw is null or DBNull ? null : (DateTimeOffset)oldestRaw;
            Invoke(JournalTestHookPhase.BeforeCommit);
            await CloseOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalOperationPurgeResult(candidates.Count, deleted, ineligible, oldest, databaseNow, effectiveCutoff);
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, Array.Empty<string>(), commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public async Task<Guid> RotateStoreEpochAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AcquireMaintenanceLockAsync(transaction, exclusive: true, cancellationToken).ConfigureAwait(false);
            Guid current = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
            Guid next;
            do next = Guid.NewGuid(); while (next == current);
            using SqlCommand update = CreateCommand(transaction, "UPDATE dbo.journal_metadata SET store_epoch = @epoch, updated_at_utc = @now WHERE metadata_key = 1;");
            Add(update, "@epoch", next);
            Add(update, "@now", Timestamp(UtcNow()));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw Corrupt(Array.Empty<string>(), "Journal metadata row is missing.");
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return next;
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, Array.Empty<string>(), commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<long> ReadSnapshotVersionAsync(string streamKey, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, "SELECT through_version FROM dbo.journal_snapshot WITH (UPDLOCK, HOLDLOCK) WHERE stream_key = @stream;");
        Add(command, "@stream", streamKey);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull) throw Corrupt(new[] { streamKey }, "Journal stream has no snapshot.");
        return Convert.ToInt64(result);
    }

    private async Task ReplaceSnapshotAsync(
        JournalCompaction compaction,
        DateTimeOffset now,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, """
            UPDATE dbo.journal_snapshot
            SET through_version = @through, snapshot_schema = @schema,
                snapshot_schema_version = @schemaVersion, data = @data,
                data_sha256 = @checksum, created_at_utc = @now
            WHERE stream_key = @stream;
            """);
        Add(command, "@through", compaction.ThroughVersion);
        Add(command, "@schema", compaction.SnapshotSchema);
        Add(command, "@schemaVersion", compaction.SnapshotSchemaVersion);
        Add(command, "@data", compaction.SnapshotData.ToArray());
        Add(command, "@checksum", compaction.SnapshotChecksum.ToArray());
        Add(command, "@now", Timestamp(now));
        Add(command, "@stream", compaction.StreamKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Validated journal snapshot disappeared inside its transaction.");
    }

    private async Task VerifyReplacementSnapshotAsync(
        JournalCompaction compaction,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, "SELECT through_version, data, data_sha256 FROM dbo.journal_snapshot WHERE stream_key = @stream;");
        Add(command, "@stream", compaction.StreamKey);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt(new[] { compaction.StreamKey }, "Replacement journal snapshot did not verify before pruning.");
        byte[] data = reader.GetFieldValue<byte[]>(1);
        byte[] checksum = reader.GetFieldValue<byte[]>(2);
        if (reader.GetInt64(0) != compaction.ThroughVersion
            || !data.AsSpan().SequenceEqual(compaction.SnapshotData.Span)
            || !checksum.AsSpan().SequenceEqual(compaction.SnapshotChecksum.Span)
            || !JournalCanonicalizer.VerifySha256(data, checksum))
            throw Corrupt(new[] { compaction.StreamKey }, "Replacement journal snapshot did not verify before pruning.");
    }
}
