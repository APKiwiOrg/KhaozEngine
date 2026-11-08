using System;

namespace KhaozEngine.Movement;

/// <summary>Opt-in, thread-owned work counts for the range-stop performance contract.</summary>
internal sealed class ArrivalPreviewDiagnostics : IDisposable
{
    [ThreadStatic] static ArrivalPreviewDiagnostics? t_current;
    readonly ArrivalPreviewDiagnostics? _previous;
    internal int FullSteps { get; private set; }
    internal int CertifiedTrials { get; private set; }

    internal ArrivalPreviewDiagnostics()
    {
        _previous = t_current;
        t_current = this;
    }

    internal static void FullStep() { if (t_current is { } current) current.FullSteps++; }
    internal static void CertifiedTrial() { if (t_current is { } current) current.CertifiedTrials++; }
    public void Dispose() => t_current = _previous;
}
