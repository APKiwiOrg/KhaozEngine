using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    public async Task<JournalOperationPurgeResult> PurgeOperationsByAgeAsync(
        JournalOperationAgePurge purge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purge);
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            await AcquireMaintenanceLockAsync(transaction, exclusive: true, cancellationToken).ConfigureAwait(false);
            await OpenOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            DateTimeOffset databaseNow = await ReadDatabaseUtcNowAsync(transaction, cancellationToken).ConfigureAwait(false);
            TimeSpan effectiveAge = purge.MinimumAge > minimumRetryHorizon ? purge.MinimumAge : minimumRetryHorizon;
            DateTimeOffset effectiveCutoff = SubtractOrMinimum(databaseNow, effectiveAge);

            using SqlCommand select = CreateCommand(transaction, """
                SELECT TOP (@limit) operation_id
                FROM dbo.journal_operation WITH (UPDLOCK, HOLDLOCK)
                WHERE retention_started_at_utc <= @cutoff
                ORDER BY retention_started_at_utc, operation_id;
                """);
            Add(select, "@cutoff", Timestamp(effectiveCutoff));
            Add(select, "@limit", purge.MaxOperations);
            var candidates = new List<Guid>();
            await using (SqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add(reader.GetGuid(0));
            }

            int deleted = 0;
            foreach (Guid operationId in candidates)
            {
                using (SqlCommand children = CreateCommand(transaction, "DELETE FROM dbo.journal_operation_stream WHERE operation_id = @id;"))
                {
                    Add(children, "@id", operationId);
                    await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using SqlCommand parent = CreateCommand(transaction, "DELETE FROM dbo.journal_operation WHERE operation_id = @id;");
                Add(parent, "@id", operationId);
                deleted += await parent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using SqlCommand oldestCommand = CreateCommand(transaction, "SELECT MIN(retention_started_at_utc) FROM dbo.journal_operation;");
            object? oldestRaw = await oldestCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? oldest = oldestRaw is null or DBNull ? null : (DateTimeOffset)oldestRaw;
            Invoke(JournalTestHookPhase.BeforeCommit);
            await CloseOperationDeleteGuardAsync(transaction, cancellationToken).ConfigureAwait(false);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalOperationPurgeResult(candidates.Count, deleted, 0, oldest, databaseNow, effectiveCutoff);
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

    private async Task<DateTimeOffset> ReadDatabaseUtcNowAsync(
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(
            transaction,
            "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');");
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTimeOffset now
            ? now
            : throw Corrupt(Array.Empty<string>(), "SQL Server did not return its UTC clock.");
    }

    private static DateTimeOffset SubtractOrMinimum(DateTimeOffset value, TimeSpan duration)
        => duration > value - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : value - duration;
}
