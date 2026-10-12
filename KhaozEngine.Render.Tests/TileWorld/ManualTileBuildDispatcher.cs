using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Holds every scheduled build body until a test runs it. Bodies run in the order and on the thread
/// the test chooses, including reverse completion and a background task. The dispatcher owns no timing: it
/// never waits, spins or runs anything on its own.</summary>
internal sealed class ManualTileBuildDispatcher : IChunkBuildDispatcher
{
    readonly object _gate = new();
    readonly List<Action> _pending = new();

    /// <summary>Bodies scheduled and not yet run.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public void Schedule(Action build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_gate) _pending.Add(build);
    }

    /// <summary>Runs one held body on the calling thread and forgets it.</summary>
    public void RunAt(int index)
    {
        Action build;
        lock (_gate)
        {
            build = _pending[index];
            _pending.RemoveAt(index);
        }
        build();
    }

    /// <summary>Runs every held body oldest first, including any a body schedules while it runs.</summary>
    public void RunAll()
    {
        while (TryTake(newest: false, out Action? build)) build();
    }

    /// <summary>Runs every held body newest first, the reverse completion order.</summary>
    public void RunReverse()
    {
        while (TryTake(newest: true, out Action? build)) build();
    }

    public void Drain() => RunAll();

    bool TryTake(bool newest, [NotNullWhen(true)] out Action? build)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                build = null;
                return false;
            }
            int index = newest ? _pending.Count - 1 : 0;
            build = _pending[index];
            _pending.RemoveAt(index);
            return true;
        }
    }
}
