using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    private async Task<OperationLookup> ResolveOperationInsertCollisionAsync(
        SqlException collision,
        JournalOperationIdentity identity,
        JournalFingerprint intent,
        SqlTransaction transaction,
        IReadOnlyList<string> streamKeys,
        CancellationToken cancellationToken)
    {
        if (!await TryRollbackAsync(transaction).ConfigureAwait(false))
            throw MapProviderFailure(collision.Number, collision, streamKeys, true, false, false);

        await using SqlConnection readConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? readTransaction = null;
        try
        {
            readTransaction = await BeginTransactionAsync(readConnection, cancellationToken).ConfigureAwait(false);
            OperationLookup lookup = await LookupOperationAsync(
                identity.OperationId,
                intent.Digest.ToArray(),
                readTransaction,
                cancellationToken,
                allowTestSuppression: false).ConfigureAwait(false);
            await readTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (lookup.Status == OperationLookupStatus.NotFound)
                throw MapProviderFailure(collision.Number, collision, streamKeys, true, false, true);
            return lookup;
        }
        catch (SqlException exception)
        {
            bool rolledBack = readTransaction is not null && await TryRollbackAsync(readTransaction).ConfigureAwait(false);
            throw MapProviderFailure(exception.Number, exception, streamKeys, readTransaction is not null, false, rolledBack);
        }
        catch (OperationCanceledException exception)
        {
            bool rolledBack = readTransaction is not null && await TryRollbackAsync(readTransaction).ConfigureAwait(false);
            throw Cancelled(streamKeys, readTransaction is not null, false, rolledBack, exception);
        }
        finally
        {
            readTransaction?.Dispose();
        }
    }

    private static async Task ThrowWriteFailureAsync(
        Exception exception,
        SqlTransaction? transaction,
        IReadOnlyList<string> streamKeys,
        bool commitStarted,
        bool committed)
    {
        if (committed) throw UnknownOutcome(streamKeys, exception);
        bool rolledBack = transaction is not null && await TryRollbackAsync(transaction).ConfigureAwait(false);
        if (exception is JournalStoreException) return;
        if (exception is SqlException sql)
            throw MapProviderFailure(sql.Number, sql, streamKeys, transaction is not null, commitStarted, rolledBack);
        if (exception is OperationCanceledException && transaction is not null)
            throw Cancelled(streamKeys, true, commitStarted, rolledBack, exception);
        if (exception is IOException or SocketException)
            throw commitStarted && !rolledBack
                ? UnknownOutcome(streamKeys, exception)
                : new JournalStoreException(
                    JournalStoreFailureKind.Unavailable,
                    JournalStoreFailureCertainty.DefinitelyNotCommitted,
                    Scope(streamKeys),
                    streamKeys,
                    "SQL Server mutation journal transport failed.",
                    exception);
        if (commitStarted)
        {
            if (!rolledBack) throw UnknownOutcome(streamKeys, exception);
            throw new JournalStoreException(
                JournalStoreFailureKind.Unavailable,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                Scope(streamKeys),
                streamKeys,
                "SQL Server mutation journal commit failed and rollback was confirmed.",
                exception);
        }
    }
}
