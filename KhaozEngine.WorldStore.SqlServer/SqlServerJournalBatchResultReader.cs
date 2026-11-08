using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.SqlServer;

internal static class SqlServerJournalBatchResultReader
{
    internal static async Task<SqlServerJournalWriteResult> ReadAsync(
        DbDataReader reader, Guid operationId, CancellationToken cancellationToken)
    {
        // Host triggers can emit result sets before the batch protocol, even inside its transaction.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new JournalStoreStateException(JournalStoreRefusal.BatchOutcomeMissing, "SQL Server mutation journal write batch returned no outcome.");
        if (reader.FieldCount == 0 || reader.IsDBNull(0) || reader.GetFieldType(0) != typeof(int))
            throw Malformed();
        var outcome = (SqlServerJournalWriteOutcome)reader.GetInt32(0);
        if (!Enum.IsDefined(outcome))
            throw new JournalStoreStateException(JournalStoreRefusal.BatchOutcomeUndefined, $"SQL Server mutation journal write batch reported outcome '{(int)outcome}'.") { Outcome = (int)outcome };
        if (reader.FieldCount != 6 || reader.IsDBNull(1) || reader.GetFieldType(1) != typeof(int)
            || !HasNullableType(reader, 2, typeof(string)) || !HasNullableType(reader, 3, typeof(int))
            || !HasNullableType(reader, 4, typeof(long)) || !HasNullableType(reader, 5, typeof(long)))
            throw Malformed();
        int lockResult = reader.GetInt32(1);
        string? limitStream = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);
        JournalStoreRefusal? limitReason = reader.IsDBNull(3) ? null : (JournalStoreRefusal)reader.GetInt32(3);
        long? limitActual = reader.IsDBNull(4) ? null : reader.GetInt64(4);
        long? limitMaximum = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        JournalCommitReceipt? receipt = outcome == SqlServerJournalWriteOutcome.Replayed
            ? await ReadReplayReceiptAsync(reader, operationId, cancellationToken).ConfigureAwait(false)
            : null;
        // Refusals and conflicts keep their rollback path, including incomplete projection limits.
        if (outcome is SqlServerJournalWriteOutcome.Applied or SqlServerJournalWriteOutcome.Replayed
            && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw Malformed();
        return new SqlServerJournalWriteResult(outcome, lockResult, limitStream, limitReason, limitActual, limitMaximum, receipt);
    }

    private static async Task<JournalCommitReceipt> ReadReplayReceiptAsync(
        DbDataReader reader,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt(JournalStoreRefusal.ReplayedOperationMissing, Array.Empty<string>(), "Replayed journal operation row could not be read inside its transaction.", operationId);
        if (!HasShape(reader, typeof(string), typeof(int), typeof(byte[]), typeof(byte[]), typeof(DateTimeOffset))
            || HasNull(reader))
            throw Malformed();
        string resultSchema = reader.GetString(0);
        int resultSchemaVersion = reader.GetInt32(1);
        byte[] resultData = reader.GetFieldValue<byte[]>(2);
        byte[] resultChecksum = reader.GetFieldValue<byte[]>(3);
        DateTimeOffset committedAt = reader.GetFieldValue<DateTimeOffset>(4);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt(JournalStoreRefusal.ReplayedStreamsMissing, Array.Empty<string>(), "Replayed journal operation streams could not be read inside its transaction.", operationId);
        if (!HasShape(reader, typeof(string), typeof(long), typeof(long), typeof(int)))
            throw Malformed();
        // A trigger can emit well-typed replay rows, with a valid checksum, that the receipt's own rules refuse.
        try
        {
            var ranges = new List<JournalStreamVersionRange>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (HasNull(reader)) throw Malformed();
                ranges.Add(new JournalStreamVersionRange(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)));
            }

            string[] streamKeys = ranges.Select(value => value.StreamKey).ToArray();
            if (!JournalCanonicalizer.VerifySha256(resultData, resultChecksum))
                throw Corrupt(JournalStoreRefusal.ResultChecksumMismatch, streamKeys, "Stored journal result checksum does not match its data.", operationId);
            return new JournalCommitReceipt(
                operationId,
                committedAt,
                ranges,
                resultSchema,
                resultSchemaVersion,
                resultData,
                resultChecksum,
                isReplay: true);
        }
        catch (ArgumentException invalid)
        {
            throw Malformed(invalid);
        }
    }

    private static bool HasNullableType(DbDataReader reader, int ordinal, Type type)
        => reader.IsDBNull(ordinal) || reader.GetFieldType(ordinal) == type;

    private static bool HasShape(DbDataReader reader, params Type[] types)
    {
        if (reader.FieldCount != types.Length) return false;
        for (int ordinal = 0; ordinal < types.Length; ordinal++)
            if (reader.GetFieldType(ordinal) != types[ordinal]) return false;
        return true;
    }

    private static bool HasNull(DbDataReader reader)
    {
        for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            if (reader.IsDBNull(ordinal)) return true;
        return false;
    }

    private static JournalStoreStateException Malformed(Exception? innerException = null)
        => new(JournalStoreRefusal.BatchResultMalformed, "SQL Server mutation journal write batch returned a malformed result.", innerException);

    internal static JournalStoreException ProjectionLimitFailure(SqlServerJournalWriteResult batch, IReadOnlyList<string> streamKeys)
    {
        if (batch.LimitStream is not string stream || batch.LimitActual is not long actual || batch.LimitMaximum is not long maximum
            || batch.LimitReason is not (JournalStoreRefusal.ProjectionSectionCountExceeded or JournalStoreRefusal.ProjectionByteCountExceeded))
            return new JournalStoreException(JournalStoreRefusal.BatchProjectionLimitIncomplete, JournalStoreFailureKind.ConstraintViolation,
                JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.OperationStreams,
                batch.LimitStream is string namedStream ? new[] { namedStream } : streamKeys,
                "Projection replacement would exceed the configured per-stream limits.") { StreamKey = batch.LimitStream };
        bool count = batch.LimitReason == JournalStoreRefusal.ProjectionSectionCountExceeded;
        return new JournalStoreException(batch.LimitReason.Value, JournalStoreFailureKind.ConstraintViolation,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.OperationStreams, new[] { stream },
            "Projection replacement would exceed the configured per-stream limits.")
        {
            StreamKey = stream,
            ActualCount = count ? actual : null, MaximumCount = count ? maximum : null,
            ActualBytes = count ? null : actual, MaximumBytes = count ? null : maximum,
        };
    }

    private static JournalStoreException Corrupt(JournalStoreRefusal reason, string[] streams, string message, Guid operationId)
        => new(reason, JournalStoreFailureKind.CorruptData, JournalStoreFailureCertainty.CommittedDataUnreadable,
            streams.Length == 0 ? JournalStoreFailureScope.WholeStore : JournalStoreFailureScope.OperationStreams,
            streams, message) { OperationId = operationId };
}
