using System;

namespace KhaozEngine.Physics;

internal enum ContactAllocationPhase
{
    Route, Acquisition, ControllerEntry, SupportRead, ShellSweep, Placement, RoundingHandoff,
    FinalClamp, Binding, Disposal, ShapeConstruction, AcquisitionBody, Preparation, PhysicsLease,
    ProducerPin, MovementLease, Fixture, SupportProposals, SupportTraversal, SupportCapture,
    SupportMaterialization, SupportExtrema, SupportContacts, SupportJoins, SupportCertification, SupportSelection,
    SupportSourceSetup, SupportColumnSetup, SupportPlanarContact, SupportPlanarHeight, SupportLocalValidation,
    SupportEdgeSetup, SupportIncidenceSetup, SupportIncidenceMeeting,
}

/// <summary>Opt-in phase boundaries. The observer owns preallocated measurement storage.</summary>
internal static class ContactAllocationDiagnostics
{
    [ThreadStatic] static Action<ContactAllocationPhase, bool>? t_observer;

    internal static void SetObserver(Action<ContactAllocationPhase, bool>? observer) => t_observer = observer;

    /// <summary>Installs <paramref name="observer"/> on this thread and returns the observer it replaced.</summary>
    internal static Action<ContactAllocationPhase, bool>? Exchange(Action<ContactAllocationPhase, bool>? observer)
    {
        Action<ContactAllocationPhase, bool>? prior = t_observer;
        t_observer = observer;
        return prior;
    }

    /// <summary>Whether a phase boundary on this thread can run observer code.</summary>
    internal static bool Observed => t_observer is not null;

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
