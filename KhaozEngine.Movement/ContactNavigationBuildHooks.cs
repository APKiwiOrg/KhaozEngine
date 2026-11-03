using System;

namespace KhaozEngine.Movement;

/// <summary>Observation points of a parallel contact build for tests, read from the calling thread when setup starts.
/// <see cref="WorkerPinned"/> and <see cref="Releasing"/> run on the calling thread, <see cref="BandCompleted"/> on the
/// worker that finished the band, with the band's index in row order. A hook that throws fails the build at that
/// point.</summary>
internal sealed class ContactNavigationBuildHooks
{
    [ThreadStatic] static ContactNavigationBuildHooks? t_current;

    internal static ContactNavigationBuildHooks? Current => t_current;

    internal Action<int>? WorkerPinned { get; init; }
    internal Action<ContactNavigationPhase, int>? BandCompleted { get; init; }
    internal Action<int>? Releasing { get; init; }
    internal Action<ContactDropCandidateMeasurement>? DropCandidate { get; init; }
    internal Action<ContactDropStepObservation>? DropStep { get; init; }
    internal bool DisableFootprintReuse { get; init; }
    internal bool DisableInitialHoldReuse { get; init; }

    /// <summary>Installs <paramref name="hooks"/> for builds started on this thread until the scope is disposed.</summary>
    internal static Scope Install(ContactNavigationBuildHooks? hooks)
    {
        ContactNavigationBuildHooks? prior = t_current;
        t_current = hooks;
        return new(prior);
    }

    internal readonly struct Scope(ContactNavigationBuildHooks? prior) : IDisposable
    {
        public void Dispose() => t_current = prior;
    }
}
