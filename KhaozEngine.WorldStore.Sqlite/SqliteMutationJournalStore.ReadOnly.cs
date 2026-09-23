using System;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore
{
    private readonly bool openedReadOnly;

    /// <summary>A read only store opens its connection with <c>Mode=ReadOnly</c> whatever the caller's connection
    /// string says, so SQLite itself refuses a write even if one got past the store.</summary>
    internal static string ReadOnlyConnectionString(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString();

    private void ThrowIfReadOnly(string operation)
    {
        if (openedReadOnly)
            throw new NotSupportedException(
                $"The SQLite mutation journal store was opened with {nameof(SqliteJournalSchemaMode)}.{nameof(SqliteJournalSchemaMode.ReadOnly)} and refuses {operation}.");
    }

    internal async Task ExecuteOnHeldConnectionForTestAsync(string sql)
    {
        using SqliteStoreLease lease = await db.EnterAsync().ConfigureAwait(false);
        using SqliteCommand command = CreateCommand(null, sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
