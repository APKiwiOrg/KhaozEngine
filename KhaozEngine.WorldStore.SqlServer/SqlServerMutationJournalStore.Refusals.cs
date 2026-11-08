using System;
using System.Collections.Generic;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    private static JournalStoreException ProjectionLimitFailure(
        string streamKey, long count, long bytes, long maximumCount, long maximumBytes,
        JournalStoreFailureKind kind, JournalStoreFailureCertainty certainty, string message)
    {
        bool countExceeded = count > maximumCount;
        return new JournalStoreException(
            countExceeded ? JournalStoreRefusal.ProjectionSectionCountExceeded : JournalStoreRefusal.ProjectionByteCountExceeded,
            kind, certainty, JournalStoreFailureScope.OperationStreams, new[] { streamKey }, message)
        {
            StreamKey = streamKey,
            ActualCount = countExceeded ? count : null,
            MaximumCount = countExceeded ? maximumCount : null,
            ActualBytes = countExceeded ? null : bytes,
            MaximumBytes = countExceeded ? null : maximumBytes,
        };
    }

    private static DateTimeOffset RequireDatabaseUtcNow(object? value)
        => value is DateTimeOffset now ? now
            : throw new JournalStoreException(JournalStoreRefusal.ProviderClockUnavailable,
                JournalStoreFailureKind.CorruptData, JournalStoreFailureCertainty.CommittedDataUnreadable,
                JournalStoreFailureScope.WholeStore, Array.Empty<string>(), "SQL Server did not return its UTC clock.")
            { ProviderName = "SQL Server" };

    private static JournalStoreException ConfirmedCommitFailure(Exception exception, IReadOnlyList<string> streamKeys)
        => new(JournalStoreRefusal.WriteFailedAfterRollback, JournalStoreFailureKind.Unavailable,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, Scope(streamKeys), streamKeys,
            "SQL Server mutation journal commit failed and rollback was confirmed.", exception)
        { Stage = JournalOperationStage.Commit };

    // The fixed scalar query normally returns the clock. Inject only its missing result at the production guard.
    internal static DateTimeOffset RefuseMissingDatabaseClockForTests(object? value) => RequireDatabaseUtcNow(value);

    // A real non-provider CommitAsync failure cannot be induced without replacing the transaction.
    internal static JournalStoreException RefuseConfirmedCommitFailureForTests(Exception cause, IReadOnlyList<string> streamKeys)
        => ConfirmedCommitFailure(cause, streamKeys);
}
