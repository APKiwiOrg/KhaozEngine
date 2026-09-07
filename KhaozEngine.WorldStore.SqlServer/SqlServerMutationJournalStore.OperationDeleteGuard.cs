using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    private async Task OpenOperationDeleteGuardAsync(
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(
            transaction,
            "CREATE TABLE #khaoz_journal_operation_delete_guard (guard bit NOT NULL);");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseOperationDeleteGuardAsync(
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(
            transaction,
            "DROP TABLE #khaoz_journal_operation_delete_guard;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
