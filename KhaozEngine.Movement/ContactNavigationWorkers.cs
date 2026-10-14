using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>The phases of a parallel contact build that run on workers, in serial order.</summary>
internal enum ContactNavigationPhase { Admission, Edges, Links }

/// <summary>A parallel contact build's workers. Setup runs on the calling thread: the snapshot, then each worker's
/// source and proof binding with its first pin, in worker order. A phase splits its rows into contiguous bands, one per
/// worker, and never schedules an empty band. Every worker owns its binding, its environment context and its anchor
/// map, and shares only the snapshot, the captured columns, the footprint and the options. Release joins nothing
/// because every phase joins its workers before returning, so it only disposes bindings in worker order.</summary>
internal sealed class ContactNavigationWorkers
{
    readonly ContactNavigationProof?[] _proofs;
    readonly EffectiveMovementBinding?[] _sources;
    readonly Action<ContactAllocationPhase, bool>?[] _observers;
    readonly WorkerDiagnosticsPlan _diagnostics;
    readonly ContactNavigationBuildHooks? _hooks;

    ContactNavigationWorkers(ContactNavigationSnapshot snapshot, int count, WorkerDiagnosticsPlan diagnostics,
        ContactNavigationBuildHooks? hooks)
    {
        Snapshot = snapshot;
        _proofs = new ContactNavigationProof?[count];
        _sources = new EffectiveMovementBinding?[count];
        _observers = new Action<ContactAllocationPhase, bool>?[count];
        _diagnostics = diagnostics;
        _hooks = hooks;
    }

    internal ContactNavigationSnapshot Snapshot { get; }
    internal int Count => _proofs.Length;

    /// <summary>Captures the snapshot, validates the envelope once and constructs at most <paramref name="degree"/>
    /// workers, never more than <paramref name="rows"/>. A setup failure releases every worker built so far.</summary>
    internal static ContactNavigationWorkers Create(PhysicsNavBake bake, GroundMoveContext context,
        EffectiveMovementBinding source, NavAreaFootprint footprint, int degree, int rows)
    {
        var snapshot = ContactNavigationSnapshot.Capture(bake, context);
        ContactNavigationEnvelope.Validate(bake, source.Tuning, source.CreateOrdinaryScope(snapshot.Frame));
        var workers = new ContactNavigationWorkers(snapshot, Math.Max(1, Math.Min(degree, rows)),
            WorkerDiagnostics.Plan(), ContactNavigationBuildHooks.Current);
        try
        {
            for (int worker = 0; worker < workers.Count; worker++)
            {
                workers._sources[worker] = context.CreateNavigationBinding(source.Tuning);
                workers._proofs[worker] = new ContactNavigationProof(bake, snapshot, workers._sources[worker]!, footprint);
                workers._observers[worker] = workers._diagnostics.Observer(worker);
                workers._hooks?.WorkerPinned?.Invoke(worker);
            }
        }
        catch (Exception failure)
        {
            workers.Release(failure);
            throw;
        }
        return workers;
    }

    /// <summary>Runs <paramref name="work"/> over <paramref name="rows"/> rows split into contiguous bands and joins
    /// every worker. Returns the cutoff holding the lowest-ordinal failure, so the caller can add failures that keep a
    /// later logical position before throwing.</summary>
    internal ContactNavigationCutoff Run(ContactNavigationPhase phase, int rows,
        Action<ContactNavigationProof, ContactNavigationBand> work)
    {
        var cutoff = new ContactNavigationCutoff();
        int count = Math.Min(Count, rows);
        if (count <= 0) return cutoff;
        var records = new WorkerDiagnosticsRecord[count];
        var threads = new Thread[count];
        int started = 0;
        try
        {
            for (; started < count; started++)
            {
                var band = new ContactNavigationBand(started, BandStart(rows, count, started),
                    BandStart(rows, count, started + 1), cutoff);
                var thread = new Thread(() => Work(phase, band, work, records))
                {
                    IsBackground = true,
                    Name = $"Contact navigation {phase} worker {band.Worker}",
                };
                threads[started] = thread;
                thread.Start();
            }
        }
        catch (Exception failure)
        {
            // A worker that could not start stops the started ones at their next item.
            cutoff.Fail(long.MinValue, failure);
        }
        finally
        {
            for (int worker = 0; worker < started; worker++) threads[worker].Join();
        }
        WorkerDiagnostics.Merge(records.AsSpan(0, started));
        return cutoff;
    }

    void Work(ContactNavigationPhase phase, ContactNavigationBand band,
        Action<ContactNavigationProof, ContactNavigationBand> work, WorkerDiagnosticsRecord[] records)
    {
        var scope = WorkerDiagnosticsScope.Enter(_diagnostics, _observers[band.Worker]);
        try
        {
            work(_proofs[band.Worker]!, band);
            _hooks?.BandCompleted?.Invoke(phase, band.Worker);
        }
        catch (Exception failure) { band.Fail(failure); }
        finally { records[band.Worker] = scope.Exit(); }
    }

    static int BandStart(int rows, int count, int band) => (int)((long)rows * band / count);

    /// <summary>Releases every worker binding in worker order after all workers joined. Caller-owned physics and
    /// borrowed views stay usable. With a <paramref name="primary"/> failure, cleanup failures are attached to it under
    /// <c>Data["CleanupFailures"]</c> and the caller rethrows the primary. Without one, a cleanup failure throws.</summary>
    internal void Release(Exception? primary)
    {
        List<Exception>? failures = null;
        for (int worker = 0; worker < Count; worker++)
        {
            try { _hooks?.Releasing?.Invoke(worker); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            try { _proofs[worker]?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            try { _sources[worker]?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            _proofs[worker] = null;
            _sources[worker] = null;
        }
        if (failures is null) return;
        var cleanup = new AggregateException("Releasing contact navigation workers failed.", failures);
        if (primary is not null)
        {
            primary.Data[CleanupFailuresKey] = cleanup;
            return;
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw cleanup;
    }

    /// <summary>The <see cref="Exception.Data"/> key under which release failures ride on the primary failure.</summary>
    internal const string CleanupFailuresKey = "CleanupFailures";
}

/// <summary>One worker's contiguous rows in one phase. <see cref="Admits"/> runs between proofs and records the serial
/// ordinal of the item about to start, so a failure carries that ordinal.</summary>
internal sealed class ContactNavigationBand(int worker, int start, int end, ContactNavigationCutoff cutoff)
{
    long _current = long.MinValue;

    internal int Worker { get; } = worker;
    internal int Start { get; } = start;
    internal int End { get; } = end;

    /// <summary>Whether the item at <paramref name="ordinal"/> may start. False once a failure at or below it exists,
    /// and every later item in the band is then above the cutoff too.</summary>
    internal bool Admits(long ordinal)
    {
        _current = ordinal;
        return cutoff.Admits(ordinal);
    }

    internal void Fail(Exception failure) => cutoff.Fail(_current, failure);
}

/// <summary>The failure cutoff of one phase. A failed item lowers the cutoff to its serial ordinal. Items above it are
/// not started, and items below it run to a terminal state. The lowest-ordinal failure is the one the serial build
/// throws first, and it is rethrown with its original type and stack.</summary>
internal sealed class ContactNavigationCutoff
{
    readonly object _gate = new();
    long _ordinal = long.MaxValue;
    Exception? _failure;

    internal bool Admits(long ordinal) => ordinal < Volatile.Read(ref _ordinal);

    internal void Fail(long ordinal, Exception failure)
    {
        lock (_gate)
        {
            if (_failure is not null && ordinal >= _ordinal) return;
            _failure = failure;
            Volatile.Write(ref _ordinal, ordinal);
        }
    }

    internal void ThrowSelected()
    {
        Exception? failure;
        lock (_gate) failure = _failure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

/// <summary>Observation points of a parallel contact build for tests, read from the calling thread when setup starts.
/// <see cref="WorkerPinned"/> and <see cref="Releasing"/> run on the calling thread, <see cref="BandCompleted"/> on the
/// worker that finished the band. A hook that throws fails the build at that point.</summary>
internal sealed class ContactNavigationBuildHooks
{
    [ThreadStatic] static ContactNavigationBuildHooks? t_current;

    internal static ContactNavigationBuildHooks? Current => t_current;

    internal Action<int>? WorkerPinned { get; init; }
    internal Action<ContactNavigationPhase, int>? BandCompleted { get; init; }
    internal Action<int>? Releasing { get; init; }

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
