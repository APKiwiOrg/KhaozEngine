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
    public async Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default)
    {
        streamKey = JournalValidation.StreamKey(streamKey);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(streamKey.Length, limits.StreamKeyCharacters, nameof(streamKey));
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand command = CreateCommand(null, """
                SELECT through_version, snapshot_schema, snapshot_schema_version, data, data_sha256, created_at_utc
                FROM journal_snapshot WHERE stream_key = $stream;
                """);
            Add(command, "$stream", streamKey);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            byte[] data = reader.GetFieldValue<byte[]>(3);
            byte[] checksum = reader.GetFieldValue<byte[]>(4);
            if (!JournalCanonicalizer.VerifySha256(data, checksum))
                throw Corrupt(new[] { streamKey }, "Stored journal snapshot checksum does not match its data.");
            var snapshot = new JournalSnapshot(
                streamKey,
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                data,
                checksum,
                Timestamp(reader.GetInt64(5)));
            return snapshot;
        }
        catch (SqliteException exception)
        {
            throw MapProviderFailure(exception, new[] { streamKey }, transactionStarted: false, commitStarted: false, rollbackConfirmed: false);
        }
    }

    public async Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        read.Validate(limits);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = db.Connection.BeginTransaction(deferred: true);
        try
        {
            (bool found, long head, long floor) = await ReadHeadAsync(read.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JournalEventPage(JournalEventPageStatus.NotFound, read.StreamKey, 0, Array.Empty<JournalStoredEvent>(), false);
            }
            long throughVersion = read.ThroughVersion ?? head;
            if (throughVersion > head)
                throw new ArgumentOutOfRangeException(nameof(read), throughVersion, "Through version cannot exceed the current stream head.");
            if (read.AfterVersion < floor)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JournalEventPage(JournalEventPageStatus.SnapshotRequired, read.StreamKey, throughVersion, Array.Empty<JournalStoredEvent>(), false);
            }

            using SqliteCommand command = CreateCommand(transaction, """
                SELECT stream_version, operation_id, operation_ordinal, event_type, event_schema_version,
                       payload, payload_sha256, committed_at_utc
                FROM journal_event
                WHERE stream_key = $stream AND stream_version > $after AND stream_version <= $through
                ORDER BY stream_version LIMIT $limit;
                """);
            Add(command, "$stream", read.StreamKey);
            Add(command, "$after", read.AfterVersion);
            Add(command, "$through", throughVersion);
            Add(command, "$limit", read.MaxEvents);
            var events = new List<JournalStoredEvent>();
            int bytes = 0;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] payload = reader.GetFieldValue<byte[]>(5);
                    if (checked(bytes + payload.Length) > read.MaxBytes) break;
                    byte[] checksum = reader.GetFieldValue<byte[]>(6);
                    if (!JournalCanonicalizer.VerifySha256(payload, checksum))
                        throw Corrupt(new[] { read.StreamKey }, "Stored journal event checksum does not match its payload.");
                    var storedEvent = new JournalStoredEvent(
                        read.StreamKey,
                        reader.GetInt64(0),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        payload,
                        checksum,
                        Timestamp(reader.GetInt64(7)));
                    events.Add(storedEvent);
                    bytes += payload.Length;
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            long lastVersion = events.Count == 0 ? read.AfterVersion : events[^1].StreamVersion;
            return new JournalEventPage(JournalEventPageStatus.Success, read.StreamKey, throughVersion, events, lastVersion >= throughVersion);
        }
        catch (SqliteException exception)
        {
            bool rolledBack = TryRollback(transaction);
            throw MapProviderFailure(exception, new[] { read.StreamKey }, transactionStarted: true, commitStarted: false, rolledBack);
        }
    }

    public async Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(query.StreamKey.Length, limits.StreamKeyCharacters, nameof(query));
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = db.Connection.BeginTransaction(deferred: true);
        try
        {
            (bool found, long head, _) = await ReadHeadAsync(query.StreamKey, transaction, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JournalProjectionRead(JournalProjectionReadStatus.NotFound, query.StreamKey, 0, Array.Empty<JournalProjectionSection>(), null);
            }
            Guid epoch = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
            string cursor = JournalProjectionCursor.Encode(epoch, query.StreamKey, head);
            bool first = query.Cursor is null;
            bool valid = JournalProjectionCursor.TryDecode(query.Cursor, out Guid cursorEpoch, out string cursorStream, out long cursorHead);
            bool reset = !first && (!valid || cursorEpoch != epoch || !StringComparer.Ordinal.Equals(cursorStream, query.StreamKey) || cursorHead > head);
            long afterVersion = first || reset ? -1 : cursorHead;

            using SqliteCommand command = CreateCommand(transaction, """
                SELECT section_name, source_version, projection_schema, projection_schema_version,
                       data, data_sha256, updated_at_utc
                FROM journal_projection
                WHERE stream_key = $stream AND source_version > $after
                ORDER BY section_name COLLATE BINARY
                LIMIT 65;
                """);
            Add(command, "$stream", query.StreamKey);
            Add(command, "$after", afterVersion);
            var sections = new List<JournalProjectionSection>();
            int bytes = 0;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] data = reader.GetFieldValue<byte[]>(4);
                    bytes = checked(bytes + data.Length);
                    byte[] checksum = reader.GetFieldValue<byte[]>(5);
                    if (!JournalCanonicalizer.VerifySha256(data, checksum))
                        throw Corrupt(new[] { query.StreamKey }, "Stored journal projection checksum does not match its data.");
                    var section = new JournalProjectionSection(
                        query.StreamKey,
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        data,
                        checksum,
                        Timestamp(reader.GetInt64(6)));
                    sections.Add(section);
                }
            }
            if (sections.Count > JournalLimits.EngineMaximumProjectionSectionsPerStream || bytes > JournalLimits.EngineMaximumAggregateProjectionBytesPerStream)
                throw Corrupt(new[] { query.StreamKey }, "Stored journal projections exceed the engine response limits.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new JournalProjectionRead(
                reset ? JournalProjectionReadStatus.ResetRequired : JournalProjectionReadStatus.Success,
                query.StreamKey,
                head,
                sections,
                cursor);
        }
        catch (SqliteException exception)
        {
            bool rolledBack = TryRollback(transaction);
            throw MapProviderFailure(exception, new[] { query.StreamKey }, transactionStarted: true, commitStarted: false, rolledBack);
        }
    }

    private async Task<OperationLookup> LookupOperationAsync(
        Guid operationId,
        byte[] intentFingerprint,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, """
            SELECT intent_fingerprint, result_schema, result_schema_version, result_data, result_sha256, committed_at_utc
            FROM journal_operation WHERE operation_id = $id;
            """);
        Add(command, "$id", OperationId(operationId));
        byte[] storedIntent;
        string resultSchema;
        int resultSchemaVersion;
        byte[] resultData;
        byte[] resultChecksum;
        DateTimeOffset committedAt;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new OperationLookup(OperationLookupStatus.NotFound);
            storedIntent = reader.GetFieldValue<byte[]>(0);
            if (!FingerprintsMatch(storedIntent, intentFingerprint))
                return new OperationLookup(OperationLookupStatus.Conflict);
            resultSchema = reader.GetString(1);
            resultSchemaVersion = reader.GetInt32(2);
            resultData = reader.GetFieldValue<byte[]>(3);
            resultChecksum = reader.GetFieldValue<byte[]>(4);
            committedAt = Timestamp(reader.GetInt64(5));
        }

        using SqliteCommand rangesCommand = CreateCommand(transaction, """
            SELECT stream_key, before_version, after_version, event_count
            FROM journal_operation_stream WHERE operation_id = $id
            ORDER BY stream_key COLLATE BINARY;
            """);
        Add(rangesCommand, "$id", OperationId(operationId));
        var ranges = new List<JournalStreamVersionRange>();
        await using (SqliteDataReader reader = await rangesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ranges.Add(new JournalStreamVersionRange(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)));
        }
        string[] streamKeys = ranges.Select(value => value.StreamKey).ToArray();
        if (!JournalCanonicalizer.VerifySha256(resultData, resultChecksum))
            throw Corrupt(streamKeys, "Stored journal result checksum does not match its data.");
        var receipt = new JournalCommitReceipt(
            operationId,
            committedAt,
            ranges,
            resultSchema,
            resultSchemaVersion,
            resultData,
            resultChecksum,
            isReplay: true);
        return new OperationLookup(OperationLookupStatus.Replayed, receipt);
    }

    private async Task<(bool Found, long Head, long RetainedFloor)> ReadHeadAsync(
        string streamKey,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, "SELECT current_version, retained_floor FROM journal_stream WHERE stream_key = $stream;");
        Add(command, "$stream", streamKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (true, reader.GetInt64(0), reader.GetInt64(1))
            : (false, 0, 0);
    }

    private async Task<Guid> ReadEpochAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, "SELECT store_epoch FROM journal_metadata WHERE metadata_key = 1;");
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string text || !Guid.TryParse(text, out Guid epoch))
            throw Corrupt(Array.Empty<string>(), "Stored journal epoch is invalid.");
        return epoch;
    }
}
