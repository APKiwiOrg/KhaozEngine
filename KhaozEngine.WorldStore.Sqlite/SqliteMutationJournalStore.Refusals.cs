using System;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    private static JournalStoreException ProjectionLimitFailure(
        string streamKey, int count, int bytes, int maximumCount, int maximumBytes,
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
        => value is null or DBNull
            ? throw new JournalStoreException(
                JournalStoreRefusal.ProviderClockUnavailable, JournalStoreFailureKind.CorruptData,
                JournalStoreFailureCertainty.CommittedDataUnreadable, JournalStoreFailureScope.WholeStore,
                Array.Empty<string>(), "SQLite did not return its UTC clock.")
            { ProviderName = "SQLite" }
            : Timestamp(Convert.ToInt64(value));

    // The fixed clock query normally returns a value. Exercise its missing-value guard without replacing the query.
    internal static DateTimeOffset RefuseMissingDatabaseClockForTests(object? value) => RequireDatabaseUtcNow(value);
}
