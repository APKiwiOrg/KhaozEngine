using System;
using System.Threading;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.Sqlite;

internal sealed class SqliteJournalTestHook
{
    private readonly Action<JournalTestHookPhase> callback;
    private int suppressedOperationLookups;

    internal SqliteJournalTestHook(Action<JournalTestHookPhase> callback, int suppressedOperationLookups = 0)
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
