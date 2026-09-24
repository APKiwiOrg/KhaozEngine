using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    // The six data tables, children before parents, because every foreign key is NO ACTION. Each batch counts the
    // table under an exclusive table lock and then deletes it, so the count is exactly what the delete removed.
    // journal_metadata is not here, and nothing below names it.
    private const string DeleteOperationStreamsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_operation_stream WITH (TABLOCKX));
        DELETE FROM dbo.journal_operation_stream;
        SELECT @rows;
        """;

    private const string DeleteEventsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_event WITH (TABLOCKX));
        DELETE FROM dbo.journal_event;
        SELECT @rows;
        """;

    private const string DeleteSnapshotsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_snapshot WITH (TABLOCKX));
        DELETE FROM dbo.journal_snapshot;
        SELECT @rows;
        """;

    private const string DeleteProjectionsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_projection WITH (TABLOCKX));
        DELETE FROM dbo.journal_projection;
        SELECT @rows;
        """;

    private const string DeleteOperationsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_operation WITH (TABLOCKX));
        DELETE FROM dbo.journal_operation;
        SELECT @rows;
        """;

    private const string DeleteStreamsSql = """
        DECLARE @rows bigint = (SELECT COUNT_BIG(*) FROM dbo.journal_stream WITH (TABLOCKX));
        DELETE FROM dbo.journal_stream;
        SELECT @rows;
        """;

    /// <summary>
    /// Deletes every row of the six data tables in ONE serializable transaction and keeps <c>journal_metadata</c> as
    /// it was. Reached only through <see cref="SqlServerJournalReset"/>, which opens the store for it.
    /// <para>
    /// The transaction's first statement takes the EXCLUSIVE side of the maintenance application lock, the gate every
    /// journal commit and initialization holds shared and compaction, purge and epoch rotation hold exclusive. A reset
    /// therefore waits up to <paramref name="lockTimeout"/> for any of them and is then refused, having changed
    /// nothing. The operation delete guard is this store's own, opened inside the transaction and dropped before the
    /// commit, exactly as the purge opens it.
    /// </para>
    /// <para>
    /// Under the lock and before the first delete it refuses a host foreign key whose delete action the deletes would
    /// fire, through <see cref="SqlServerJournalHostKeys"/>, so that refusal also changes nothing.
    /// </para>
    /// </summary>
    internal async Task<JournalResetResult> ResetJournalAsync(TimeSpan lockTimeout, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly(nameof(ResetJournalAsync));
        cancellationToken.ThrowIfCancellationRequested();
        int lockTimeoutMilliseconds = checked((int)Math.Ceiling(lockTimeout.TotalMilliseconds));
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AcquireResetLockAsync(transaction, lockTimeout, lockTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            await SqlServerJournalHostKeys.RefuseFiringKeysAsync(transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            await OpenOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            Guid epoch = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
            long operationStreams = await DeleteEveryRowAsync(transaction, DeleteOperationStreamsSql, cancellationToken).ConfigureAwait(false);
            long events = await DeleteEveryRowAsync(transaction, DeleteEventsSql, cancellationToken).ConfigureAwait(false);
            long snapshots = await DeleteEveryRowAsync(transaction, DeleteSnapshotsSql, cancellationToken).ConfigureAwait(false);
            long projections = await DeleteEveryRowAsync(transaction, DeleteProjectionsSql, cancellationToken).ConfigureAwait(false);
            long operations = await DeleteEveryRowAsync(transaction, DeleteOperationsSql, cancellationToken).ConfigureAwait(false);
            long streams = await DeleteEveryRowAsync(transaction, DeleteStreamsSql, cancellationToken).ConfigureAwait(false);
            await CloseOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            var result = new JournalResetResult(epoch, streams, events, snapshots, projections, operations, operationStreams);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return result;
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, Array.Empty<string>(), commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// The exclusive maintenance lock, taken through the store's own statement with the reset's own wait. The
    /// command itself is given the store's command timeout, which the reset sets longer than the wait, so the lock
    /// answers first and a refusal always names it.
    /// </summary>
    private async Task AcquireResetLockAsync(
        SqlTransaction transaction,
        TimeSpan lockTimeout,
        int lockTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await AcquireMaintenanceLockAsync(transaction, exclusive: true, lockTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalStoreException locked)
        {
            throw new JournalStoreException(
                locked.Kind,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                JournalStoreFailureScope.WholeStore,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The journal reset could not take the exclusive maintenance lock within {lockTimeout.TotalSeconds:0.###} seconds, because a journal writer or maintenance call held it. Nothing was deleted. Stop every journal host and retry."),
                locked);
        }
    }

    private async Task<long> DeleteEveryRowAsync(SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, sql);
        object? rows = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return rows is long count
            ? count
            : throw new InvalidOperationException("A journal reset delete reported no row count.");
    }
}
