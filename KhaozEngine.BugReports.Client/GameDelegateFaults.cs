using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Logs the failures of one game delegate the flow calls, once per distinct failure: a delegate that throws the same
/// exception every frame logs one warning, and a success clears the record so a later failure logs again.
/// Thread-safe.
/// </summary>
internal sealed class GameDelegateFaults(string name)
{
    readonly object _sync = new();
    string? _last;

    /// <summary>Warns about <paramref name="exception"/> unless it repeats the failure logged last.</summary>
    internal void Report(Exception exception)
    {
        string key = exception.GetType().FullName + ": " + SafeMessage(exception);
        lock (_sync)
        {
            if (string.Equals(key, _last, StringComparison.Ordinal)) return;
            _last = key;
        }
        Log.Warn("The game's bug report " + name + " delegate failed. Bug reports carry on without it.", exception);
    }

    /// <summary>Records a call that succeeded, so the next failure is logged.</summary>
    internal void Clear()
    {
        lock (_sync) _last = null;
    }

    static string SafeMessage(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
