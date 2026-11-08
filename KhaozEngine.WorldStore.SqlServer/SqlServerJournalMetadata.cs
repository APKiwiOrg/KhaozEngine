using System.Data.Common;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.SqlServer;

internal static class SqlServerJournalMetadata
{
    internal static string ReadDefinition(DbDataReader reader, int ordinal, string objectName)
    {
        if (reader.IsDBNull(ordinal))
            throw new JournalStoreException(
                JournalStoreRefusal.DefinitionUnavailable,
                JournalStoreFailureKind.SchemaMismatch,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                JournalStoreFailureScope.WholeStore,
                null,
                $"Cannot read SQL Server journal definition '{objectName}'. " +
                "Grant VIEW DEFINITION on the owning dbo.journal_* table and use unencrypted definitions.")
            { ObjectName = objectName, RequiredPermission = "VIEW DEFINITION" };
        return reader.GetString(ordinal);
    }
}
