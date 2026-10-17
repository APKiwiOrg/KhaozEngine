using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The sends whose bundle is still being built, up to the moment each reaches the offline queue. Shutdown waits on
/// them, so a report sent just before the game closes is written to the queue first. Thread-safe.
/// </summary>
internal sealed class ReportBuildTracker
{
    readonly object _sync = new();
    readonly List<Task> _building = new();

    /// <summary>Starts tracking one build. Complete the returned source when the build reached the queue or failed.</summary>
    internal TaskCompletionSource Start()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _building.RemoveAll(static build => build.IsCompleted);
            _building.Add(reached.Task);
        }
        return reached;
    }

    /// <summary>Blocks until every tracked build reached the queue, or <paramref name="timeout"/> passed.</summary>
    /// <returns>False when a build was still running at the timeout.</returns>
    internal bool WaitAll(TimeSpan timeout)
    {
        Task[] building;
        lock (_sync)
        {
            _building.RemoveAll(static build => build.IsCompleted);
            building = _building.ToArray();
        }
        return building.Length == 0 || Task.WaitAll(building, timeout);
    }
}
