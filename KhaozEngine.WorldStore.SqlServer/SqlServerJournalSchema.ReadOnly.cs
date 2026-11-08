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
    /// Validates the version-three schema for <see cref="SqlServerJournalSchemaMode.ReadOnly"/>. Every command is a
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
            throw Failure(JournalStoreFailureKind.Cancelled, "Read only schema validation was cancelled before it began.", exception, JournalOperationStage.SchemaReadOnlyOpen);
        }
        catch (SqlException exception)
        {
            throw ProviderFailure(exception.Number, JournalOperationStage.SchemaReadOnlyOpen, exception, cancellationToken.IsCancellationRequested);
        }

        await using (transaction)
        {
            try
            {
                if (testHook is not null)
                    await testHook.InvokeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (await CountJournalObjectsAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false) == 0)
                    throw Mismatch(JournalStoreRefusal.SchemaMissing, "missing");
                int declaredVersion = await ReadDeclaredVersionAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                if (declaredVersion != CurrentVersion) throw UnsupportedVersion(declaredVersion);
                await ValidateShapeAsync(connection, transaction, commandTimeoutSeconds, CurrentVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw Failure(JournalStoreFailureKind.Cancelled, "Read only schema validation was cancelled.", exception, JournalOperationStage.SchemaReadOnlyValidation);
            }
            catch (SqlException exception)
            {
                throw ProviderFailure(exception.Number, JournalOperationStage.SchemaReadOnlyValidation, exception, cancellationToken.IsCancellationRequested);
            }
            finally
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
            }
        }
    }
}
