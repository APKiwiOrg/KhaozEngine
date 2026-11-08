using System;
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
        return await SqlServerJournalBatchResultReader.ReadAsync(reader, operationId, cancellationToken).ConfigureAwait(false);
    }

    private static Exception WriteBatchFailure(SqlServerJournalWriteResult batch)
        => batch.Outcome switch
        {
            SqlServerJournalWriteOutcome.ApplicationLock => SqlServerJournalSchema.ApplicationLockFailure(batch.LockResult),
            SqlServerJournalWriteOutcome.ProjectionLimit => SqlServerJournalBatchResultReader.ProjectionLimitFailure(batch),
            // The serializable head locks exclude concurrent deletion after validation.
            SqlServerJournalWriteOutcome.StreamVanished =>
                new InvalidOperationException("Validated journal stream disappeared inside its transaction."),
            _ => new JournalStoreStateException(JournalStoreRefusal.BatchOutcomeUnsupported,
                $"SQL Server mutation journal write batch reported an unsupported outcome '{batch.Outcome}'.") { Outcome = (int)batch.Outcome },
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
