using System;

namespace KhaozEngine.Physics;

/// <summary>Carries opt-in diagnostics across worker threads. A plan copies the calling thread's enablement and label, never its
/// observer delegates. Allocation observers use the per-worker factory and Find observers require the separate opt-in
/// test hook on the calling thread. Each worker records into a fresh recorder and restores its prior state on exit, and the
/// caller merges every worker record after the join.</summary>
internal static class WorkerDiagnostics
{
    [ThreadStatic] static Func<int, Action<ContactAllocationPhase, bool>?>? t_observerFactory;
    [ThreadStatic] static Action<SupportFindObservation>? t_findObserver;

    /// <summary>Opt-in test hook copied into each executing worker. Returns the prior hook for caller restoration.</summary>
    internal static Action<SupportFindObservation>? ExchangeFindObserver(Action<SupportFindObservation>? observer)
    {
        Action<SupportFindObservation>? prior = t_findObserver;
        t_findObserver = observer;
        return prior;
    }

    /// <summary>Installs, for worker builds started on this thread, the factory that gives worker <c>i</c> its own
    /// allocation observer. Null gives workers none.</summary>
    internal static void SetObserverFactory(Func<int, Action<ContactAllocationPhase, bool>?>? factory) =>
        t_observerFactory = factory;

    /// <summary>The calling thread's enablement, immutable label and explicit observer opt-ins.</summary>
    internal static WorkerDiagnosticsPlan Plan() => new(SupportQueryDiagnostics.Enabled, t_observerFactory, t_findObserver,
        SupportQueryDiagnostics.MeasurementLabel);

    /// <summary>Adds worker records to the calling thread's recorder.</summary>
    internal static void Merge(ReadOnlySpan<WorkerDiagnosticsRecord> records)
    {
        foreach (WorkerDiagnosticsRecord record in records)
        {
            SupportQueryDiagnostics.Merge(record.Counts, record.Routes);
            SupportQueryDiagnostics.MergeProofReuseRetention(record.Retention, record.HasCompleteLifetimes);
        }
    }
}

internal readonly record struct WorkerDiagnosticsPlan(bool Enabled,
    Func<int, Action<ContactAllocationPhase, bool>?>? ObserverFactory,
    Action<SupportFindObservation>? FindObserver = null, string? MeasurementLabel = null)
{
    /// <summary>Worker <paramref name="worker"/>'s allocation observer. Runs on the calling thread during setup.</summary>
    internal Action<ContactAllocationPhase, bool>? Observer(int worker) => ObserverFactory?.Invoke(worker);
}

internal readonly record struct WorkerDiagnosticsRecord(SupportQueryCounts Counts, SupportRouteCounts Routes,
    SupportProofReuseRetention Retention = default, string? MeasurementLabel = null, bool HasCompleteLifetimes = true);

/// <summary>One worker's recorder on its own thread. Entering replaces every thread-local counter, observation and
/// callback, and exiting returns the record and restores what was there.</summary>
internal readonly struct WorkerDiagnosticsScope
{
    readonly SupportQueryDiagnostics.ThreadState _support;
    readonly Action<ContactAllocationPhase, bool>? _observer;

    WorkerDiagnosticsScope(in SupportQueryDiagnostics.ThreadState support, Action<ContactAllocationPhase, bool>? observer)
    {
        _support = support;
        _observer = observer;
    }

    internal static WorkerDiagnosticsScope Enter(in WorkerDiagnosticsPlan plan, Action<ContactAllocationPhase, bool>? observer)
    {
        SupportQueryDiagnostics.ThreadState support =
            SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(plan.Enabled) with
            { Find = plan.FindObserver, MeasurementLabel = plan.MeasurementLabel });
        return new(support, ContactAllocationDiagnostics.Exchange(observer));
    }

    internal WorkerDiagnosticsRecord Exit()
    {
        try
        {
            return new(SupportQueryDiagnostics.Snapshot(), SupportQueryDiagnostics.Routes,
                SupportQueryDiagnostics.Retention, SupportQueryDiagnostics.MeasurementLabel,
                SupportQueryDiagnostics.HasCompleteLifetimes);
        }
        finally { Restore(); }
    }

    /// <summary>Restores recorder ownership and callbacks, carrying the worker's current physical retention balance.
    /// Disabled workers use this path without materializing a measurement record.</summary>
    internal void Restore()
    {
        SupportQueryDiagnostics.Restore(_support);
        ContactAllocationDiagnostics.Exchange(_observer);
    }
}
