using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    public async Task<JournalOperationPurgeResult> PurgeOperationsByAgeAsync(
        JournalOperationAgePurge purge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purge);
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = db.Connection.BeginTransaction(deferred: false);
            OpenOperationDeleteGuard();
            DateTimeOffset databaseNow = await ReadDatabaseUtcNowAsync(transaction, cancellationToken).ConfigureAwait(false);
            TimeSpan effectiveAge = purge.MinimumAge > minimumRetryHorizon ? purge.MinimumAge : minimumRetryHorizon;
            DateTimeOffset effectiveCutoff = SubtractOrMinimum(databaseNow, effectiveAge);

            using SqliteCommand select = CreateCommand(transaction, """
                SELECT operation_id
                FROM journal_operation
                WHERE retention_started_at_utc <= $cutoff
                ORDER BY retention_started_at_utc, operation_id COLLATE BINARY
                LIMIT $limit;
                """);
            Add(select, "$cutoff", Timestamp(effectiveCutoff));
            Add(select, "$limit", purge.MaxOperations);
            var candidates = new List<string>();
            await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add(reader.GetString(0));
            }

            int deleted = 0;
            foreach (string operationId in candidates)
            {
                using (SqliteCommand children = CreateCommand(transaction, "DELETE FROM journal_operation_stream WHERE operation_id = $id;"))
                {
                    Add(children, "$id", operationId);
                    await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqliteCommand parent = CreateCommand(transaction, "DELETE FROM journal_operation WHERE operation_id = $id;");
                Add(parent, "$id", operationId);
                deleted += await parent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using SqliteCommand oldestCommand = CreateCommand(transaction, "SELECT MIN(retention_started_at_utc) FROM journal_operation;");
            object? oldestRaw = await oldestCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? oldest = oldestRaw is null or DBNull ? null : Timestamp(Convert.ToInt64(oldestRaw));
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalOperationPurgeResult(candidates.Count, deleted, 0, oldest, databaseNow, effectiveCutoff);
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

    private async Task<DateTimeOffset> ReadDatabaseUtcNowAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(
            transaction,
            "SELECT CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER);");
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw Corrupt(Array.Empty<string>(), "SQLite did not return its UTC clock.")
            : Timestamp(Convert.ToInt64(value));
    }

    private static DateTimeOffset SubtractOrMinimum(DateTimeOffset value, TimeSpan duration)
        => duration > value - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : value - duration;
}
