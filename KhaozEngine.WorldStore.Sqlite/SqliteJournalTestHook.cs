using System;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.Sqlite;

internal sealed class SqliteJournalTestHook
{
    private readonly Action<JournalTestHookPhase> callback;

    internal SqliteJournalTestHook(Action<JournalTestHookPhase> callback)
        => this.callback = callback ?? throw new ArgumentNullException(nameof(callback));

    internal void Invoke(JournalTestHookPhase phase) => callback(phase);
}
