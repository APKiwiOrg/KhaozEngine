using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    public async Task<JournalInitializeResult> InitializeAsync(
        JournalInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        cancellationToken.ThrowIfCancellationRequested();
        initialization.Identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(initialization.Identity);
        string[] streamKeys = { initialization.AbsentStreamKey };
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            OperationLookup lookup = await LookupOperationAsync(
                initialization.Identity.OperationId,
                intent.Digest.ToArray(),
                transaction,
                cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            if (lookup.Status != OperationLookupStatus.NotFound)
            {
                transaction.Rollback();
                return lookup.Status == OperationLookupStatus.Conflict
                    ? new JournalInitializeResult(JournalInitializeStatus.OperationConflict)
                    : new JournalInitializeResult(JournalInitializeStatus.Replayed, lookup.Receipt);
            }

            initialization.Validate(limits);
            (bool found, _, _) = await ReadHeadAsync(initialization.AbsentStreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (found)
            {
                transaction.Rollback();
                return new JournalInitializeResult(JournalInitializeStatus.ExistingStream);
            }
            Invoke(JournalTestHookPhase.AfterHeadValidation);

            DateTimeOffset now = UtcNow();
            await InsertInitializedStreamAsync(initialization, now, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterEventWrites);
            await WriteInitializationProjectionsAsync(initialization, now, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            var range = new JournalStreamVersionRange(initialization.AbsentStreamKey, 0, 0, 0);
            JournalFingerprint execution = JournalCanonicalizer.CreateInitializationFingerprint(initialization);
            await InsertOperationAsync(
                initialization.Identity,
                intent,
                execution,
                initialization.ResultSchema,
                initialization.ResultSchemaVersion,
                initialization.ResultData.ToArray(),
                initialization.ResultChecksum.ToArray(),
                now,
                new[] { range },
                transaction,
                cancellationToken).ConfigureAwait(false);
            var receipt = new JournalCommitReceipt(
                initialization.Identity.OperationId,
                now,
                new[] { range },
                initialization.ResultSchema,
                initialization.ResultSchemaVersion,
                initialization.ResultData.ToArray(),
                initialization.ResultChecksum.ToArray());
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalInitializeResult(JournalInitializeStatus.Initialized, receipt);
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

    public async Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        commit.Identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(commit.Identity);
        string[] streamKeys = commit.StreamMutations.Select(value => value.StreamKey).ToArray();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            OperationLookup lookup = await LookupOperationAsync(
                commit.Identity.OperationId,
                intent.Digest.ToArray(),
                transaction,
                cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            if (lookup.Status != OperationLookupStatus.NotFound)
            {
                transaction.Rollback();
                return lookup.Status == OperationLookupStatus.Conflict
                    ? new JournalCommitResult(JournalCommitStatus.OperationConflict)
                    : new JournalCommitResult(JournalCommitStatus.Replayed, lookup.Receipt);
            }

            commit.Validate(limits);
            var heads = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (JournalStreamMutation mutation in commit.StreamMutations)
            {
                (bool found, long head, _) = await ReadHeadAsync(mutation.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
                if (!found || head != mutation.ExpectedVersion)
                {
                    transaction.Rollback();
                    return new JournalCommitResult(JournalCommitStatus.VersionConflict);
                }
                heads.Add(mutation.StreamKey, head);
            }
            await VerifyProjectionLimitsAsync(commit.ProjectionWrites, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterHeadValidation);

            DateTimeOffset now = UtcNow();
            var ranges = new List<JournalStreamVersionRange>(commit.StreamMutations.Count);
            int operationOrdinal = 0;
            foreach (JournalStreamMutation mutation in commit.StreamMutations)
            {
                long before = heads[mutation.StreamKey];
                long version = before;
                foreach (JournalEvent journalEvent in mutation.Events)
                {
                    version = checked(version + 1);
                    await InsertEventAsync(
                        mutation.StreamKey,
                        version,
                        commit.Identity.OperationId,
                        operationOrdinal++,
                        journalEvent,
                        now,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                }
                if (version != before)
                    await UpdateStreamHeadAsync(mutation.StreamKey, version, now, transaction, cancellationToken).ConfigureAwait(false);
                ranges.Add(new JournalStreamVersionRange(mutation.StreamKey, before, version, mutation.Events.Count));
                heads[mutation.StreamKey] = version;
            }
            Invoke(JournalTestHookPhase.AfterEventWrites);

            foreach (JournalProjectionWrite projection in commit.ProjectionWrites)
                await UpsertProjectionAsync(projection, heads[projection.StreamKey], now, transaction, cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            JournalFingerprint execution = JournalCanonicalizer.CreateCommitFingerprint(commit);
            await InsertOperationAsync(
                commit.Identity,
                intent,
                execution,
                commit.ResultSchema,
                commit.ResultSchemaVersion,
                commit.ResultData.ToArray(),
                commit.ResultChecksum.ToArray(),
                now,
                ranges,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var receipt = new JournalCommitReceipt(
                commit.Identity.OperationId,
                now,
                ranges,
                commit.ResultSchema,
                commit.ResultSchemaVersion,
                commit.ResultData.ToArray(),
                commit.ResultChecksum.ToArray());
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalCommitResult(JournalCommitStatus.Applied, receipt);
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

    private async Task InsertInitializedStreamAsync(
        JournalInitialization initialization,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (SqliteCommand stream = CreateCommand(transaction, "INSERT INTO journal_stream(stream_key, current_version, retained_floor, updated_at_utc) VALUES ($stream, 0, 0, $now);"))
        {
            Add(stream, "$stream", initialization.AbsentStreamKey);
            Add(stream, "$now", Timestamp(now));
            await stream.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using SqliteCommand snapshot = CreateCommand(transaction, """
            INSERT INTO journal_snapshot(
                stream_key, through_version, snapshot_schema, snapshot_schema_version,
                data, data_sha256, created_at_utc)
            VALUES ($stream, 0, $schema, $schemaVersion, $data, $checksum, $now);
            """);
        Add(snapshot, "$stream", initialization.AbsentStreamKey);
        Add(snapshot, "$schema", initialization.SnapshotSchema);
        Add(snapshot, "$schemaVersion", initialization.SnapshotSchemaVersion);
        Add(snapshot, "$data", initialization.SnapshotData.ToArray());
        Add(snapshot, "$checksum", initialization.SnapshotChecksum.ToArray());
        Add(snapshot, "$now", Timestamp(now));
        await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteInitializationProjectionsAsync(
        JournalInitialization initialization,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (JournalProjectionWrite projection in initialization.ProjectionWrites)
            await UpsertProjectionAsync(projection, 0, now, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertEventAsync(
        string streamKey,
        long version,
        Guid operationId,
        int ordinal,
        JournalEvent journalEvent,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, """
            INSERT INTO journal_event(
                stream_key, stream_version, operation_id, operation_ordinal, event_type,
                event_schema_version, payload, payload_sha256, committed_at_utc)
            VALUES ($stream, $version, $operation, $ordinal, $type, $schemaVersion, $payload, $checksum, $now);
            """);
        Add(command, "$stream", streamKey);
        Add(command, "$version", version);
        Add(command, "$operation", OperationId(operationId));
        Add(command, "$ordinal", ordinal);
        Add(command, "$type", journalEvent.EventType);
        Add(command, "$schemaVersion", journalEvent.EventSchemaVersion);
        Add(command, "$payload", journalEvent.Payload.ToArray());
        Add(command, "$checksum", journalEvent.PayloadChecksum.ToArray());
        Add(command, "$now", Timestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateStreamHeadAsync(
        string streamKey,
        long version,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, "UPDATE journal_stream SET current_version = $version, updated_at_utc = $now WHERE stream_key = $stream;");
        Add(command, "$version", version);
        Add(command, "$now", Timestamp(now));
        Add(command, "$stream", streamKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Validated journal stream disappeared inside its transaction.");
    }

    private async Task UpsertProjectionAsync(
        JournalProjectionWrite projection,
        long sourceVersion,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, """
            INSERT INTO journal_projection(
                stream_key, section_name, source_version, projection_schema,
                projection_schema_version, data, data_sha256, updated_at_utc)
            VALUES ($stream, $section, $version, $schema, $schemaVersion, $data, $checksum, $now)
            ON CONFLICT(stream_key, section_name) DO UPDATE SET
                source_version = excluded.source_version,
                projection_schema = excluded.projection_schema,
                projection_schema_version = excluded.projection_schema_version,
                data = excluded.data,
                data_sha256 = excluded.data_sha256,
                updated_at_utc = excluded.updated_at_utc;
            """);
        Add(command, "$stream", projection.StreamKey);
        Add(command, "$section", projection.SectionName);
        Add(command, "$version", sourceVersion);
        Add(command, "$schema", projection.ProjectionSchema);
        Add(command, "$schemaVersion", projection.ProjectionSchemaVersion);
        Add(command, "$data", projection.Data.ToArray());
        Add(command, "$checksum", projection.DataChecksum.ToArray());
        Add(command, "$now", Timestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertOperationAsync(
        JournalOperationIdentity identity,
        JournalFingerprint intent,
        JournalFingerprint execution,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData,
        byte[] resultChecksum,
        DateTimeOffset now,
        IReadOnlyList<JournalStreamVersionRange> ranges,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (SqliteCommand operation = CreateCommand(transaction, """
            INSERT INTO journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES ($id, $kind, $intentFormat, $intent, $executionFormat, $execution,
                    $resultSchema, $resultSchemaVersion, $result, $resultChecksum, $now);
            """))
        {
            Add(operation, "$id", OperationId(identity.OperationId));
            Add(operation, "$kind", identity.ActionKind);
            Add(operation, "$intentFormat", intent.FormatVersion);
            Add(operation, "$intent", intent.Digest.ToArray());
            Add(operation, "$executionFormat", execution.FormatVersion);
            Add(operation, "$execution", execution.Digest.ToArray());
            Add(operation, "$resultSchema", resultSchema);
            Add(operation, "$resultSchemaVersion", resultSchemaVersion);
            Add(operation, "$result", resultData);
            Add(operation, "$resultChecksum", resultChecksum);
            Add(operation, "$now", Timestamp(now));
            await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (JournalStreamVersionRange range in ranges)
        {
            using SqliteCommand child = CreateCommand(transaction, """
                INSERT INTO journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count)
                VALUES ($id, $stream, $before, $after, $count);
                """);
            Add(child, "$id", OperationId(identity.OperationId));
            Add(child, "$stream", range.StreamKey);
            Add(child, "$before", range.BeforeVersion);
            Add(child, "$after", range.AfterVersion);
            Add(child, "$count", range.EventCount);
            await child.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task VerifyProjectionLimitsAsync(
        IReadOnlyList<JournalProjectionWrite> writes,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<string, JournalProjectionWrite> group in writes.GroupBy(value => value.StreamKey, StringComparer.Ordinal))
        {
            using SqliteCommand command = CreateCommand(transaction, "SELECT section_name, length(data) FROM journal_projection WHERE stream_key = $stream;");
            Add(command, "$stream", group.Key);
            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) sizes.Add(reader.GetString(0), reader.GetInt32(1));
            }
            foreach (JournalProjectionWrite write in group) sizes[write.SectionName] = write.Data.Length;
            int bytes = 0;
            foreach (int value in sizes.Values) bytes = checked(bytes + value);
            if (sizes.Count > limits.ProjectionSectionsPerStream || bytes > limits.AggregateProjectionBytesPerStream)
                throw new JournalStoreException(
                    JournalStoreFailureKind.ConstraintViolation,
                    JournalStoreFailureCertainty.DefinitelyNotCommitted,
                    JournalStoreFailureScope.OperationStreams,
                    new[] { group.Key },
                    "Projection replacement would exceed the configured per-stream limits.");
        }
    }

    private static void ThrowWriteFailure(
        Exception exception,
        SqliteTransaction? transaction,
        IReadOnlyList<string> streamKeys,
        bool commitStarted,
        bool committed)
    {
        if (committed) throw UnknownOutcome(streamKeys, exception);
        bool rolledBack = transaction is not null && TryRollback(transaction);
        if (exception is JournalStoreException) return;
        if (exception is SqliteException sqlite)
            throw MapProviderFailure(sqlite, streamKeys, transaction is not null, commitStarted, rolledBack);
        if (exception is OperationCanceledException && transaction is not null)
            throw new JournalStoreException(
                commitStarted && !rolledBack ? JournalStoreFailureKind.UnknownOutcome : JournalStoreFailureKind.Cancelled,
                commitStarted && !rolledBack ? JournalStoreFailureCertainty.Unknown : JournalStoreFailureCertainty.DefinitelyNotCommitted,
                streamKeys.Count == 0 ? JournalStoreFailureScope.WholeStore : JournalStoreFailureScope.OperationStreams,
                streamKeys,
                "SQLite mutation journal operation was cancelled.",
                exception);
    }
}
