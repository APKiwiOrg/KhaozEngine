using System;
using System.IO;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>Schema failure classification, independent of SQL transport and transaction lifetime.</summary>
internal static partial class SqlServerJournalSchema
{
    private static JournalStoreException ProviderFailure(int number, JournalOperationStage stage, Exception exception,
        bool cancelled, bool rollbackConfirmed = true)
    {
        if (!rollbackConfirmed) return Unknown("Schema initialization failed and rollback could not be confirmed.", exception);
        JournalStoreFailureKind kind = cancelled ? JournalStoreFailureKind.Cancelled
            : stage is JournalOperationStage.SchemaOpen or JournalOperationStage.SchemaReadOnlyOpen ? JournalStoreFailureKind.Unavailable
            : number switch
            {
                1205 => JournalStoreFailureKind.Deadlock,
                1222 or -2 => JournalStoreFailureKind.Timeout,
                _ => JournalStoreFailureKind.Unavailable,
            };
        string message = stage switch
        {
            JournalOperationStage.SchemaOpen => cancelled
                ? "Schema initialization was cancelled before a transaction began."
                : "SQL Server journal schema could not open a connection.",
            JournalOperationStage.SchemaReadOnlyOpen => cancelled
                ? "Read only schema validation was cancelled before it began."
                : "SQL Server journal schema could not be opened for read only validation.",
            JournalOperationStage.SchemaTransactionBegin => kind switch
            {
                JournalStoreFailureKind.Cancelled => "Schema initialization was cancelled before the transaction began.",
                JournalStoreFailureKind.Deadlock => "Schema initialization was a deadlock victim before transaction admission.",
                JournalStoreFailureKind.Timeout => "Schema initialization timed out before transaction admission.",
                _ => "SQL Server journal schema could not begin its transaction.",
            },
            JournalOperationStage.SchemaReadOnlyValidation => kind switch
            {
                JournalStoreFailureKind.Cancelled => "Read only schema validation was cancelled.",
                JournalStoreFailureKind.Deadlock => "Read only schema validation was a deadlock victim.",
                JournalStoreFailureKind.Timeout => "Read only schema validation timed out.",
                _ => "SQL Server journal read only schema validation failed.",
            },
            _ => kind switch
            {
                JournalStoreFailureKind.Cancelled => "Schema initialization was cancelled.",
                JournalStoreFailureKind.Deadlock => "Schema initialization was a deadlock victim.",
                JournalStoreFailureKind.Timeout => "Schema initialization timed out.",
                _ => "SQL Server journal schema initialization failed.",
            },
        };
        return Failure(kind, message, exception, stage, cancelled ? null : number);
    }

    private static JournalStoreException Failure(JournalStoreFailureKind kind, string message, Exception exception,
        JournalOperationStage stage = JournalOperationStage.SchemaInitialization, int? providerCode = null)
        => new(kind == JournalStoreFailureKind.Cancelled ? JournalStoreRefusal.SchemaCancelled : JournalStoreRefusal.SchemaProviderFailure,
            kind, JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.WholeStore, null, message, exception)
        { Stage = stage, ProviderCode = providerCode };

    private static JournalStoreException Unknown(string message, Exception exception)
        => new(JournalStoreRefusal.SchemaRollbackUnconfirmed, JournalStoreFailureKind.UnknownOutcome,
            JournalStoreFailureCertainty.Unknown, JournalStoreFailureScope.WholeStore, null, message, exception);

    /// <summary>Injects a provider number and rollback result at the schema failure classifier, without a live fault.</summary>
    internal static void RefuseSchemaProviderFailureForTests(int number, JournalOperationStage stage,
        bool cancelled = false, bool rollbackConfirmed = true)
        => throw ProviderFailure(number, stage, new IOException("Injected schema provider failure."), cancelled, rollbackConfirmed);
}
