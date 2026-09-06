using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

internal sealed class SqlServerJournalTestHook
{
    private readonly Action<JournalTestHookPhase> callback;
    private int suppressedOperationLookups;

    internal SqlServerJournalTestHook(Action<JournalTestHookPhase> callback, int suppressedOperationLookups = 0)
    {
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        this.suppressedOperationLookups = suppressedOperationLookups;
    }

    internal void Invoke(JournalTestHookPhase phase) => callback(phase);

    internal bool SuppressOperationLookup()
    {
        if (Volatile.Read(ref suppressedOperationLookups) <= 0) return false;
        return Interlocked.Decrement(ref suppressedOperationLookups) >= 0;
    }
}

internal sealed class SqlServerJournalSchemaTestHook(
    Func<SqlConnection, SqlTransaction, CancellationToken, Task> callback)
{
    private readonly Func<SqlConnection, SqlTransaction, CancellationToken, Task> callback =
        callback ?? throw new ArgumentNullException(nameof(callback));

    internal Task InvokeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
        => callback(connection, transaction, cancellationToken);
}
