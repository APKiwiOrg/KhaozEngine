using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

internal sealed record SqlServerJournalWriteResult(
    SqlServerJournalWriteOutcome Outcome,
    int LockResult,
    string? LimitStream,
    JournalStoreRefusal? LimitReason,
    long? LimitActual,
    long? LimitMaximum,
    JournalCommitReceipt? Receipt);

public sealed partial class SqlServerMutationJournalStore
{
    public SqlServerJournalCommitStatistics CommitStatistics => counters.Snapshot();

    private int MaintenanceLockTimeoutMilliseconds
        => (int)Math.Min((long)commandTimeoutSeconds * 1000L, int.MaxValue);

    private bool SuppressOperationLookup() => testHook?.SuppressOperationLookup() == true;

    private async Task<SqlServerJournalWriteResult> ExecuteWriteBatchAsync(
        SqlCommand command,
        Guid operationId,
        SqlServerJournalCommandTally tally,
        CancellationToken cancellationToken)
    {
        tally.Increment();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        // Both private batch builders always emit one outcome row. Provider failures throw before a result.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQL Server mutation journal write batch returned no outcome.");
        var outcome = (SqlServerJournalWriteOutcome)reader.GetInt32(0);
        int lockResult = reader.GetInt32(1);
        string? limitStream = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);
        JournalStoreRefusal? limitReason = reader.IsDBNull(3) ? null : (JournalStoreRefusal)reader.GetInt32(3);
        long? limitActual = reader.IsDBNull(4) ? null : reader.GetInt64(4);
        long? limitMaximum = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        // The private SQL producer assigns only the outcome constants in this enum.
        if (!Enum.IsDefined(outcome))
            throw new InvalidOperationException($"SQL Server mutation journal write batch reported outcome '{(int)outcome}'.");
        JournalCommitReceipt? receipt = outcome == SqlServerJournalWriteOutcome.Replayed
            ? await ReadReplayReceiptAsync(reader, operationId, cancellationToken).ConfigureAwait(false)
            : null;
        return new SqlServerJournalWriteResult(outcome, lockResult, limitStream, limitReason, limitActual, limitMaximum, receipt);
    }

    private static async Task<JournalCommitReceipt> ReadReplayReceiptAsync(
        SqlDataReader reader,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // Replayed is emitted only after finding the operation under a serializable update lock.
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Replayed journal operation row could not be read inside its transaction.");
        string resultSchema = reader.GetString(0);
        int resultSchemaVersion = reader.GetInt32(1);
        byte[] resultData = reader.GetFieldValue<byte[]>(2);
        byte[] resultChecksum = reader.GetFieldValue<byte[]>(3);
        DateTimeOffset committedAt = reader.GetFieldValue<DateTimeOffset>(4);

        // The fixed replay arm always emits the stream result set, even when it contains no rows.
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Replayed journal operation streams could not be read inside its transaction.");
        var ranges = new List<JournalStreamVersionRange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ranges.Add(new JournalStreamVersionRange(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)));

        string[] streamKeys = ranges.Select(value => value.StreamKey).ToArray();
        if (!JournalCanonicalizer.VerifySha256(resultData, resultChecksum))
            throw Corrupt(JournalStoreRefusal.ResultChecksumMismatch, streamKeys, "Stored journal result checksum does not match its data.", operationId: operationId);
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

    private static Exception WriteBatchFailure(SqlServerJournalWriteResult batch)
        => batch.Outcome switch
        {
            SqlServerJournalWriteOutcome.ApplicationLock => SqlServerJournalSchema.ApplicationLockFailure(batch.LockResult),
            SqlServerJournalWriteOutcome.ProjectionLimit => ProjectionBatchLimitFailure(batch),
            // The serializable head locks exclude concurrent deletion after validation.
            SqlServerJournalWriteOutcome.StreamVanished =>
                new InvalidOperationException("Validated journal stream disappeared inside its transaction."),
            // The callers handle every successful and conflict outcome before entering this failure adapter.
            _ => new InvalidOperationException(
                $"SQL Server mutation journal write batch reported an unsupported outcome '{batch.Outcome}'."),
        };

    private static JournalStoreException ProjectionBatchLimitFailure(SqlServerJournalWriteResult batch)
    {
        // Only the private limit branch emits this outcome and fills all four limit columns together.
        if (batch.LimitStream is not string stream || batch.LimitActual is not long actual || batch.LimitMaximum is not long maximum
            || batch.LimitReason is not (JournalStoreRefusal.ProjectionSectionCountExceeded or JournalStoreRefusal.ProjectionByteCountExceeded))
            throw new InvalidOperationException("SQL Server mutation journal write batch returned an incomplete projection limit.");
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

    internal static SqlCommand BuildCommitBatchForTest(
        JournalCommit commit,
        JournalLimits limits,
        bool skipOperationLookup = false)
    {
        var command = new SqlCommand();
        SqlServerJournalWriteBatch.BuildCommit(
            command,
            commit,
            JournalCanonicalizer.CreateIntentFingerprint(commit.Identity),
            JournalCanonicalizer.CreateCommitFingerprint(commit),
            DateTimeOffset.UnixEpoch,
            limits,
            30_000,
            skipOperationLookup);
        return command;
    }

    internal static SqlCommand BuildInitializeBatchForTest(JournalInitialization initialization)
    {
        var command = new SqlCommand();
        SqlServerJournalWriteBatch.BuildInitialize(
            command,
            initialization,
            JournalCanonicalizer.CreateIntentFingerprint(initialization.Identity),
            JournalCanonicalizer.CreateInitializationFingerprint(initialization),
            DateTimeOffset.UnixEpoch,
            30_000,
            skipOperationLookup: false);
        return command;
    }
}
