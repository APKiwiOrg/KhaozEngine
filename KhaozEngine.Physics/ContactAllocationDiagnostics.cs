using System;

namespace KhaozEngine.Physics;

internal enum ContactAllocationPhase
{
    Route, Acquisition, ControllerEntry, SupportRead, ShellSweep, Placement, RoundingHandoff,
    FinalClamp, Binding, Disposal, ShapeConstruction, AcquisitionBody, Preparation, PhysicsLease,
    ProducerPin, MovementLease, FixtureWitnessComparison,
}

/// <summary>Opt-in phase boundaries. The observer owns preallocated measurement storage.</summary>
internal static class ContactAllocationDiagnostics
{
    [ThreadStatic] static Action<ContactAllocationPhase, bool>? t_observer;

    internal static void SetObserver(Action<ContactAllocationPhase, bool>? observer) => t_observer = observer;

    internal static Scope Begin(ContactAllocationPhase phase)
    {
        Action<ContactAllocationPhase, bool>? observer = t_observer;
        observer?.Invoke(phase, true);
        return new(phase, observer);
    }

    internal readonly struct Scope(ContactAllocationPhase phase, Action<ContactAllocationPhase, bool>? observer) : IDisposable
    {
        public void Dispose() => observer?.Invoke(phase, false);
    }
}
