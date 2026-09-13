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
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQL Server mutation journal write batch returned no outcome.");
        var outcome = (SqlServerJournalWriteOutcome)reader.GetInt32(0);
        int lockResult = reader.GetInt32(1);
        string? limitStream = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);
        if (!Enum.IsDefined(outcome))
            throw new InvalidOperationException($"SQL Server mutation journal write batch reported outcome '{(int)outcome}'.");
        JournalCommitReceipt? receipt = outcome == SqlServerJournalWriteOutcome.Replayed
            ? await ReadReplayReceiptAsync(reader, operationId, cancellationToken).ConfigureAwait(false)
            : null;
        return new SqlServerJournalWriteResult(outcome, lockResult, limitStream, receipt);
    }

    private static async Task<JournalCommitReceipt> ReadReplayReceiptAsync(
        SqlDataReader reader,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt(Array.Empty<string>(), "Replayed journal operation row could not be read inside its transaction.");
        string resultSchema = reader.GetString(0);
        int resultSchemaVersion = reader.GetInt32(1);
        byte[] resultData = reader.GetFieldValue<byte[]>(2);
        byte[] resultChecksum = reader.GetFieldValue<byte[]>(3);
        DateTimeOffset committedAt = reader.GetFieldValue<DateTimeOffset>(4);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt(Array.Empty<string>(), "Replayed journal operation streams could not be read inside its transaction.");
        var ranges = new List<JournalStreamVersionRange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ranges.Add(new JournalStreamVersionRange(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)));

        string[] streamKeys = ranges.Select(value => value.StreamKey).ToArray();
        if (!JournalCanonicalizer.VerifySha256(resultData, resultChecksum))
            throw Corrupt(streamKeys, "Stored journal result checksum does not match its data.");
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

    private static Exception WriteBatchFailure(SqlServerJournalWriteResult batch, IReadOnlyList<string> streamKeys)
        => batch.Outcome switch
        {
            SqlServerJournalWriteOutcome.ApplicationLock => SqlServerJournalSchema.ApplicationLockFailure(batch.LockResult),
            SqlServerJournalWriteOutcome.ProjectionLimit => new JournalStoreException(
                JournalStoreFailureKind.ConstraintViolation,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                JournalStoreFailureScope.OperationStreams,
                batch.LimitStream is string stream ? new[] { stream } : streamKeys,
                "Projection replacement would exceed the configured per-stream limits."),
            SqlServerJournalWriteOutcome.StreamVanished =>
                new InvalidOperationException("Validated journal stream disappeared inside its transaction."),
            _ => new InvalidOperationException(
                $"SQL Server mutation journal write batch reported an unsupported outcome '{batch.Outcome}'."),
        };

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
