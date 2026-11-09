using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Opt-in, thread-owned evidence work. Installation work is outside query intervals.</summary>
internal sealed class StaticEvidenceDiagnostics : IDisposable
{
    [ThreadStatic] static StaticEvidenceDiagnostics? t_current;
    readonly StaticEvidenceDiagnostics? _previous;
    internal int OwnersVisited { get; private set; }
    internal int TrianglesExpanded { get; private set; }

    internal StaticEvidenceDiagnostics()
    {
        _previous = t_current;
        t_current = this;
    }

    internal static void Owner() { if (t_current is { } current) current.OwnersVisited++; }
    internal static void Triangle() { if (t_current is { } current) current.TrianglesExpanded++; }
    public void Dispose() => t_current = _previous;
}
