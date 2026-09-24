using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    // The six data tables, children before parents, so every delete runs with foreign keys on and none of them
    // leaves a row referencing one already gone. journal_metadata is not here, and nothing below names it.
    private const string DeleteOperationStreamsSql = "DELETE FROM journal_operation_stream;";
    private const string DeleteEventsSql = "DELETE FROM journal_event;";
    private const string DeleteSnapshotsSql = "DELETE FROM journal_snapshot;";
    private const string DeleteProjectionsSql = "DELETE FROM journal_projection;";
    private const string DeleteOperationsSql = "DELETE FROM journal_operation;";
    private const string DeleteStreamsSql = "DELETE FROM journal_stream;";

    /// <summary>
    /// Deletes every row of the six data tables in ONE immediate transaction and keeps <c>journal_metadata</c> as it
    /// was. Reached only through <see cref="SqliteJournalReset"/>, which opens the store for it.
    /// <para>
    /// The immediate transaction is the write lock every SQLite journal writer takes for its own transaction, so a
    /// reset waits for a writer on another connection up to the busy timeout and is then refused with
    /// <see cref="JournalStoreFailureKind.Timeout"/>, having changed nothing. The operation delete guard is this
    /// store's own, opened after the lock is held and closed however the transaction ends, exactly as the purge
    /// opens it.
    /// </para>
    /// </summary>
    internal async Task<JournalResetResult> ResetJournalAsync(CancellationToken cancellationToken)
    {
        ThrowIfReadOnly(nameof(ResetJournalAsync));
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = BeginResetTransaction();
            OpenOperationDeleteGuard();
            Guid epoch = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
            long operationStreams = await DeleteEveryRowAsync(transaction, DeleteOperationStreamsSql, cancellationToken).ConfigureAwait(false);
            long events = await DeleteEveryRowAsync(transaction, DeleteEventsSql, cancellationToken).ConfigureAwait(false);
            long snapshots = await DeleteEveryRowAsync(transaction, DeleteSnapshotsSql, cancellationToken).ConfigureAwait(false);
            long projections = await DeleteEveryRowAsync(transaction, DeleteProjectionsSql, cancellationToken).ConfigureAwait(false);
            long operations = await DeleteEveryRowAsync(transaction, DeleteOperationsSql, cancellationToken).ConfigureAwait(false);
            long streams = await DeleteEveryRowAsync(transaction, DeleteStreamsSql, cancellationToken).ConfigureAwait(false);
            var result = new JournalResetResult(epoch, streams, events, snapshots, projections, operations, operationStreams);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return result;
        }
        catch (Exception exception)
        {
            ThrowWriteFailure(exception, transaction, Array.Empty<string>(), commitStarted, committed);
            throw;
        }
        finally
        {
            CloseOperationDeleteGuard();
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// The immediate transaction, with a busy or locked file answered as the reset's own refusal rather than a bare
    /// provider failure. The provider error stays the inner exception.
    /// </summary>
    private SqliteTransaction BeginResetTransaction()
    {
        try
        {
            return db.Connection.BeginTransaction(deferred: false);
        }
        catch (SqliteException busy) when (busy.SqliteErrorCode is 5 or 6)
        {
            throw new JournalStoreException(
                JournalStoreFailureKind.Timeout,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                JournalStoreFailureScope.WholeStore,
                null,
                "The journal reset could not take the SQLite write lock before its lock timeout, because another journal writer held it. Nothing was deleted. Stop every journal host and retry.",
                busy);
        }
    }

    /// <summary>
    /// One whole-table delete, answering the rows it removed. SQLite counts only the rows the statement itself
    /// deleted, never a trigger's work, and the count stays exact when it empties the table in one step.
    /// </summary>
    private async Task<long> DeleteEveryRowAsync(SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(transaction, sql);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
