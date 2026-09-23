using System;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    private readonly bool openedReadOnly;

    private void ThrowIfReadOnly(string operation)
    {
        if (openedReadOnly)
            throw new NotSupportedException(
                $"The SQL Server mutation journal store was opened with {nameof(SqlServerJournalSchemaMode)}.{nameof(SqlServerJournalSchemaMode.ReadOnly)} and refuses {operation}.");
    }
}
