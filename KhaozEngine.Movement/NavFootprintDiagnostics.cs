using System;

namespace KhaozEngine.Movement;

/// <summary>Opt-in, thread-owned count of exact footprint geometry calls.</summary>
internal sealed class NavFootprintDiagnostics : IDisposable
{
    [ThreadStatic] private static NavFootprintDiagnostics? t_current;
    private readonly NavFootprintDiagnostics? _previous;

    internal static NavFootprintDiagnostics? Current => t_current;
    internal long CandidateCells { get; private set; }

    internal NavFootprintDiagnostics()
    {
        _previous = t_current;
        t_current = this;
    }

    internal void CountRow(int first, int last)
    {
        if (first <= last) CandidateCells += (long)last - first + 1;
    }

    public void Dispose() => t_current = _previous;
}
