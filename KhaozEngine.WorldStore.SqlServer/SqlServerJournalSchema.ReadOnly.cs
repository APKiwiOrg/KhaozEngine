using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

internal static partial class SqlServerJournalSchema
{
    /// <summary>
    /// Validates the version-two schema for <see cref="SqlServerJournalSchemaMode.ReadOnly"/>. Every command is a
    /// catalog or metadata <c>SELECT</c> inside one read committed transaction that always ends in a rollback. It
    /// takes no application lock, because that lock only orders writers, and it never creates or migrates: a missing
    /// or older schema is a <c>SchemaMismatch</c> refusal naming the migration a writer would apply.
    /// </summary>
    internal static async Task ValidateReadOnlyAsync(
        string connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        SqlServerJournalSchemaTestHook? testHook = null)
    {
        await using var connection = new SqlConnection(connectionString);
        SqlTransaction transaction;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(JournalStoreFailureKind.Cancelled, "Read only schema validation was cancelled before it began.", exception);
        }
        catch (SqlException exception)
        {
            throw Failure(JournalStoreFailureKind.Unavailable, "SQL Server journal schema could not be opened for read only validation.", exception);
        }

        await using (transaction)
        {
            try
            {
                if (testHook is not null)
                    await testHook.InvokeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (await CountJournalObjectsAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false) == 0)
                    throw Mismatch("missing");
                int declaredVersion = await ReadDeclaredVersionAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                if (declaredVersion != CurrentVersion) throw Mismatch($"unsupported version '{declaredVersion}'");
                await ValidateShapeAsync(connection, transaction, commandTimeoutSeconds, CurrentVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw Failure(JournalStoreFailureKind.Cancelled, "Read only schema validation was cancelled.", exception);
            }
            catch (SqlException exception)
            {
                throw exception.Number switch
                {
                    1205 => Failure(JournalStoreFailureKind.Deadlock, "Read only schema validation was a deadlock victim.", exception),
                    1222 or -2 => Failure(JournalStoreFailureKind.Timeout, "Read only schema validation timed out.", exception),
                    _ => Failure(JournalStoreFailureKind.Unavailable, "SQL Server journal read only schema validation failed.", exception),
                };
            }
            finally
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
            }
        }
    }
}
