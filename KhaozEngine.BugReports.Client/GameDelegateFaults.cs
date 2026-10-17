using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Logs the failures of one game delegate the flow calls: one warning when it starts failing, then nothing until a
/// call succeeds again, whatever the later exceptions say. A message that changes on every call therefore cannot log
/// every frame and flood <see cref="RecentLogSink"/>. Thread-safe.
/// </summary>
internal sealed class GameDelegateFaults(string name)
{
    readonly object _sync = new();
    bool _failing;

    /// <summary>Warns about <paramref name="exception"/> unless the delegate already failed since its last success.</summary>
    internal void Report(Exception exception)
    {
        lock (_sync)
        {
            if (_failing) return;
            _failing = true;
        }
        Log.Warn("The game's bug report " + name + " delegate failed. Bug reports carry on without it until it succeeds.",
            exception);
    }

    /// <summary>Records a call that succeeded, so the next failure is logged.</summary>
    internal void Clear()
    {
        lock (_sync) _failing = false;
    }
}
