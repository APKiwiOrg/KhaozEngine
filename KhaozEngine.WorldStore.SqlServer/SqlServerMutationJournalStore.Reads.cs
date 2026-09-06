using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    public async Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default)
    {
        streamKey = JournalValidation.StreamKey(streamKey);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(streamKey.Length, limits.StreamKeyCharacters, nameof(streamKey));
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqlCommand command = CreateCommand(connection, null, """
                SELECT through_version, snapshot_schema, snapshot_schema_version, data, data_sha256, created_at_utc
                FROM dbo.journal_snapshot WHERE stream_key = @stream;
                """);
            Add(command, "@stream", streamKey);
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
                reader.GetFieldValue<DateTimeOffset>(5));
            return snapshot;
        }
        catch (SqlException exception)
        {
            throw MapProviderFailure(exception.Number, exception, new[] { streamKey }, false, false, false);
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(new[] { streamKey }, false, false, true, exception);
        }
    }

    public async Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        read.Validate(limits);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
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

            using SqlCommand command = CreateCommand(transaction, """
                SELECT TOP (@limit) stream_version, operation_id, operation_ordinal, event_type, event_schema_version,
                       payload, payload_sha256, committed_at_utc
                FROM dbo.journal_event
                WHERE stream_key = @stream AND stream_version > @after AND stream_version <= @through
                ORDER BY stream_version;
                """);
            Add(command, "@stream", read.StreamKey);
            Add(command, "@after", read.AfterVersion);
            Add(command, "@through", throughVersion);
            Add(command, "@limit", read.MaxEvents);
            var events = new List<JournalStoredEvent>();
            int bytes = 0;
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
                        reader.GetGuid(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        payload,
                        checksum,
                        reader.GetFieldValue<DateTimeOffset>(7));
                    events.Add(storedEvent);
                    bytes += payload.Length;
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            long lastVersion = events.Count == 0 ? read.AfterVersion : events[^1].StreamVersion;
            return new JournalEventPage(JournalEventPageStatus.Success, read.StreamKey, throughVersion, events, lastVersion >= throughVersion);
        }
        catch (SqlException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw MapProviderFailure(exception.Number, exception, new[] { read.StreamKey }, false, false, rolledBack);
        }
        catch (OperationCanceledException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw Cancelled(new[] { read.StreamKey }, false, false, rolledBack, exception);
        }
    }

    public async Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(query.StreamKey.Length, limits.StreamKeyCharacters, nameof(query));
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
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

            using SqlCommand command = CreateCommand(transaction, """
                SELECT TOP (65) section_name, source_version, projection_schema, projection_schema_version,
                       data, data_sha256, updated_at_utc
                FROM dbo.journal_projection
                WHERE stream_key = @stream AND source_version > @after
                ORDER BY section_name COLLATE Latin1_General_100_BIN2;
                """);
            Add(command, "@stream", query.StreamKey);
            Add(command, "@after", afterVersion);
            var sections = new List<JournalProjectionSection>();
            int bytes = 0;
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
                        reader.GetFieldValue<DateTimeOffset>(6));
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
        catch (SqlException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw MapProviderFailure(exception.Number, exception, new[] { query.StreamKey }, false, false, rolledBack);
        }
        catch (OperationCanceledException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw Cancelled(new[] { query.StreamKey }, false, false, rolledBack, exception);
        }
    }

    private async Task<OperationLookup> LookupOperationAsync(
        Guid operationId,
        byte[] intentFingerprint,
        SqlTransaction transaction,
        CancellationToken cancellationToken,
        bool allowTestSuppression = true,
        bool forUpdate = false)
    {
        if (allowTestSuppression && testHook?.SuppressOperationLookup() == true)
            return new OperationLookup(OperationLookupStatus.NotFound);
        string operationSql = forUpdate
            ? """
                SELECT intent_fingerprint, result_schema, result_schema_version, result_data, result_sha256, committed_at_utc
                FROM dbo.journal_operation WITH (UPDLOCK, HOLDLOCK) WHERE operation_id = @id;
                """
            : """
                SELECT intent_fingerprint, result_schema, result_schema_version, result_data, result_sha256, committed_at_utc
                FROM dbo.journal_operation WHERE operation_id = @id;
                """;
        using SqlCommand command = CreateCommand(transaction, operationSql);
        Add(command, "@id", operationId);
        byte[] storedIntent;
        string resultSchema;
        int resultSchemaVersion;
        byte[] resultData;
        byte[] resultChecksum;
        DateTimeOffset committedAt;
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
            committedAt = reader.GetFieldValue<DateTimeOffset>(5);
        }

        using SqlCommand rangesCommand = CreateCommand(transaction, """
            SELECT stream_key, before_version, after_version, event_count
            FROM dbo.journal_operation_stream WHERE operation_id = @id
            ORDER BY stream_key COLLATE Latin1_General_100_BIN2;
            """);
        Add(rangesCommand, "@id", operationId);
        var ranges = new List<JournalStreamVersionRange>();
        await using (SqlDataReader reader = await rangesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
        SqlTransaction transaction,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        string sql = forUpdate
            ? "SELECT current_version, retained_floor FROM dbo.journal_stream WITH (UPDLOCK, HOLDLOCK) WHERE stream_key = @stream;"
            : "SELECT current_version, retained_floor FROM dbo.journal_stream WHERE stream_key = @stream;";
        using SqlCommand command = CreateCommand(transaction, sql);
        Add(command, "@stream", streamKey);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (true, reader.GetInt64(0), reader.GetInt64(1))
            : (false, 0, 0);
    }

    private async Task<Guid> ReadEpochAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, "SELECT store_epoch FROM dbo.journal_metadata WHERE metadata_key = 1;");
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not Guid epoch)
            throw Corrupt(Array.Empty<string>(), "Stored journal epoch is invalid.");
        return epoch;
    }
}
