using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    public async Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        cancellationToken.ThrowIfCancellationRequested();
        compaction.Validate(limits);
        string[] streamKeys = { compaction.StreamKey };
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            (bool found, long head, _) = await ReadHeadAsync(compaction.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                transaction.Rollback();
                return new JournalCompactionResult(JournalCompactionStatus.NotFound, 0, 0, 0);
            }
            long previousVersion = await ReadSnapshotVersionAsync(compaction.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (head < compaction.ThroughVersion || compaction.ThroughVersion <= previousVersion)
            {
                transaction.Rollback();
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
                using (SqliteCommand prune = CreateCommand(transaction, "DELETE FROM journal_event WHERE stream_key = $stream AND stream_version <= $through;"))
                {
                    Add(prune, "$stream", compaction.StreamKey);
                    Add(prune, "$through", pruneThrough);
                    pruned = await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqliteCommand floor = CreateCommand(transaction, """
                    UPDATE journal_stream
                    SET retained_floor = max(retained_floor, $through), updated_at_utc = $now
                    WHERE stream_key = $stream;
                    """);
                Add(floor, "$through", pruneThrough);
                Add(floor, "$now", Timestamp(now));
                Add(floor, "$stream", compaction.StreamKey);
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
            ThrowWriteFailure(exception, transaction, streamKeys, commitStarted, committed);
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
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            OpenOperationDeleteGuard();
            DateTimeOffset databaseNow = await ReadDatabaseUtcNowAsync(transaction, cancellationToken).ConfigureAwait(false);
            DateTimeOffset safeCutoff = SubtractOrMinimum(databaseNow, minimumRetryHorizon);
            DateTimeOffset effectiveCutoff = purge.CutoffUtc < safeCutoff ? purge.CutoffUtc : safeCutoff;
            using SqliteCommand select = CreateCommand(transaction, """
                SELECT operation_id, retention_started_at_utc
                FROM journal_operation
                WHERE committed_at_utc <= $cutoff
                ORDER BY committed_at_utc, operation_id COLLATE BINARY
                LIMIT $limit;
                """);
            Add(select, "$cutoff", Timestamp(effectiveCutoff));
            Add(select, "$limit", purge.MaxOperations);
            var candidates = new List<(string OperationId, long RetentionStartedAt)>();
            await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add((reader.GetString(0), reader.GetInt64(1)));
            }

            long safeCutoffTimestamp = Timestamp(safeCutoff);
            int ineligible = 0;
            int deleted = 0;
            foreach ((string operationId, long retentionStartedAt) in candidates)
            {
                if (retentionStartedAt > safeCutoffTimestamp)
                {
                    ineligible++;
                    continue;
                }
                using (SqliteCommand children = CreateCommand(transaction, "DELETE FROM journal_operation_stream WHERE operation_id = $id;"))
                {
                    Add(children, "$id", operationId);
                    await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqliteCommand parent = CreateCommand(transaction, "DELETE FROM journal_operation WHERE operation_id = $id;");
                Add(parent, "$id", operationId);
                deleted += await parent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using SqliteCommand oldestCommand = CreateCommand(transaction, "SELECT MIN(committed_at_utc) FROM journal_operation;");
            object? oldestRaw = await oldestCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? oldest = oldestRaw is null or DBNull ? null : Timestamp(Convert.ToInt64(oldestRaw));
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalOperationPurgeResult(candidates.Count, deleted, ineligible, oldest, databaseNow, effectiveCutoff);
        }
        catch (Exception exception)
        {
            ThrowWriteFailure(exception, transaction, Array.Empty<string>(), commitStarted, committed);
            throw;
        }
        finally
        {
            CloseOperationDeleteGuard();
            transaction?.Dispose();
        }
    }

    public async Task<Guid> RotateStoreEpochAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            Guid current = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
            Guid next;
            do next = Guid.NewGuid(); while (next == current);
            using SqliteCommand update = CreateCommand(transaction, "UPDATE journal_metadata SET store_epoch = $epoch, updated_at_utc = $now WHERE metadata_key = 1;");
            Add(update, "$epoch", next.ToString("D"));
            Add(update, "$now", Timestamp(UtcNow()));
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
            ThrowWriteFailure(exception, transaction, Array.Empty<string>(), commitStarted, committed);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<long> ReadSnapshotVersionAsync(string streamKey, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, "SELECT through_version FROM journal_snapshot WHERE stream_key = $stream;");
        Add(command, "$stream", streamKey);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull) throw Corrupt(new[] { streamKey }, "Journal stream has no snapshot.");
        return Convert.ToInt64(result);
    }

    private async Task ReplaceSnapshotAsync(
        JournalCompaction compaction,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, """
            UPDATE journal_snapshot
            SET through_version = $through, snapshot_schema = $schema,
                snapshot_schema_version = $schemaVersion, data = $data,
                data_sha256 = $checksum, created_at_utc = $now
            WHERE stream_key = $stream;
            """);
        Add(command, "$through", compaction.ThroughVersion);
        Add(command, "$schema", compaction.SnapshotSchema);
        Add(command, "$schemaVersion", compaction.SnapshotSchemaVersion);
        Add(command, "$data", compaction.SnapshotData.ToArray());
        Add(command, "$checksum", compaction.SnapshotChecksum.ToArray());
        Add(command, "$now", Timestamp(now));
        Add(command, "$stream", compaction.StreamKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Validated journal snapshot disappeared inside its transaction.");
    }

    private async Task VerifyReplacementSnapshotAsync(
        JournalCompaction compaction,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, "SELECT through_version, data, data_sha256 FROM journal_snapshot WHERE stream_key = $stream;");
        Add(command, "$stream", compaction.StreamKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != compaction.ThroughVersion
            || !JournalCanonicalizer.VerifySha256(reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2)))
            throw Corrupt(new[] { compaction.StreamKey }, "Replacement journal snapshot did not verify before pruning.");
    }
}
