using System;

namespace KhaozEngine.Physics;

/// <summary>Carries opt-in diagnostics across worker threads. A plan copies the calling thread's enablement, never its
/// observer delegates. A caller observer reaches a worker only through the per-worker observer factory installed on the
/// calling thread. Each worker records into a fresh recorder and restores its thread's prior state on exit, and the
/// caller merges every worker record after the join.</summary>
internal static class WorkerDiagnostics
{
    [ThreadStatic] static Func<int, Action<ContactAllocationPhase, bool>?>? t_observerFactory;

    /// <summary>Installs, for worker builds started on this thread, the factory that gives worker <c>i</c> its own
    /// allocation observer. Null gives workers none.</summary>
    internal static void SetObserverFactory(Func<int, Action<ContactAllocationPhase, bool>?>? factory) =>
        t_observerFactory = factory;

    /// <summary>The calling thread's enablement and observer factory.</summary>
    internal static WorkerDiagnosticsPlan Plan() => new(SupportQueryDiagnostics.Enabled, t_observerFactory);

    /// <summary>Adds worker records to the calling thread's recorder.</summary>
    internal static void Merge(ReadOnlySpan<WorkerDiagnosticsRecord> records)
    {
        foreach (WorkerDiagnosticsRecord record in records)
        {
            SupportQueryDiagnostics.Merge(record.Counts, record.Routes);
            SupportQueryDiagnostics.MergeProofReuseRetention(record.Retention);
        }
    }
}

internal readonly record struct WorkerDiagnosticsPlan(bool Enabled,
    Func<int, Action<ContactAllocationPhase, bool>?>? ObserverFactory)
{
    /// <summary>Worker <paramref name="worker"/>'s allocation observer. Runs on the calling thread during setup.</summary>
    internal Action<ContactAllocationPhase, bool>? Observer(int worker) => ObserverFactory?.Invoke(worker);
}

internal readonly record struct WorkerDiagnosticsRecord(SupportQueryCounts Counts, SupportRouteCounts Routes,
    SupportProofReuseRetention Retention = default);

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
            SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(plan.Enabled));
        return new(support, ContactAllocationDiagnostics.Exchange(observer));
    }

    internal WorkerDiagnosticsRecord Exit()
    {
        var record = new WorkerDiagnosticsRecord(SupportQueryDiagnostics.Snapshot(), SupportQueryDiagnostics.Routes,
            SupportQueryDiagnostics.Retention);
        SupportQueryDiagnostics.Exchange(_support);
        ContactAllocationDiagnostics.Exchange(_observer);
        return record;
    }
}
