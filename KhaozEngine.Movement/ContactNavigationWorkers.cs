using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>The phases of a parallel contact build that run on workers, in serial order.</summary>
internal enum ContactNavigationPhase { Admission, Edges, Links }

/// <summary>A parallel contact build's workers. Setup runs on the calling thread: the snapshot, then each worker's
/// source and proof binding with its first pin, in worker order. A phase splits its rows into contiguous bands, up to
/// <see cref="BandsPerWorker"/> per worker and never an empty one, and the workers claim bands in ascending order.
/// Every worker owns its binding, its environment context and its anchor map, and shares only the snapshot, the
/// captured columns, the footprint and the options. Release joins nothing because every phase joins its workers before
/// returning, so it only disposes bindings in worker order.</summary>
internal sealed class ContactNavigationWorkers
{
    /// <summary>Bands per worker in one phase. Rows differ in proof cost, so a worker that finishes a cheap band claims
    /// the next one instead of idling behind a costly band. The count is fixed, so the partition never depends on
    /// timing.</summary>
    internal const int BandsPerWorker = 4;

    readonly ContactNavigationProof?[] _proofs;
    readonly EffectiveMovementBinding?[] _sources;
    readonly Action<ContactAllocationPhase, bool>?[] _observers;
    readonly WorkerDiagnosticsPlan _diagnostics;
    readonly ContactNavigationBuildHooks? _hooks;
    readonly ContactNavigationReusePolicy _reuse;

    ContactNavigationWorkers(ContactNavigationSnapshot snapshot, int count, WorkerDiagnosticsPlan diagnostics,
        ContactNavigationBuildHooks? hooks, ContactNavigationReusePolicy reuse)
    {
        Snapshot = snapshot;
        _proofs = new ContactNavigationProof?[count];
        _sources = new EffectiveMovementBinding?[count];
        _observers = new Action<ContactAllocationPhase, bool>?[count];
        _diagnostics = diagnostics;
        _hooks = hooks;
        _reuse = reuse;
    }

    internal ContactNavigationSnapshot Snapshot { get; }
    internal int Count => _proofs.Length;

    /// <summary>Captures the snapshot, validates the envelope once and constructs at most <paramref name="degree"/>
    /// workers, never more than <paramref name="rows"/>. A setup failure releases every worker built so far.</summary>
    internal static ContactNavigationWorkers Create(PhysicsNavBake bake, GroundMoveContext context,
        EffectiveMovementBinding source, NavAreaFootprint footprint, int degree, int rows,
        ContactNavigationReusePolicy reuse)
    {
        var snapshot = ContactNavigationSnapshot.Capture(bake, context);
        ContactNavigationEnvelope.Validate(bake, source.Tuning, source.CreateOrdinaryScope(snapshot.Frame));
        var workers = new ContactNavigationWorkers(snapshot, Math.Max(1, Math.Min(degree, rows)),
            WorkerDiagnostics.Plan(), ContactNavigationBuildHooks.Current, reuse);
        try
        {
            for (int worker = 0; worker < workers.Count; worker++)
            {
                workers._sources[worker] = context.CreateNavigationBinding(source.Tuning);
                workers._proofs[worker] = new ContactNavigationProof(bake, snapshot, workers._sources[worker]!, footprint, reuse);
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

    /// <summary>The bands one phase splits <paramref name="rows"/> into with <paramref name="workers"/> workers:
    /// <see cref="BandsPerWorker"/> per worker, capped by the row count so no band is empty.</summary>
    internal static int BandCount(int workers, int rows) => (int)Math.Min(rows, (long)workers * BandsPerWorker);

    /// <summary>Runs <paramref name="work"/> over <paramref name="rows"/> rows split into contiguous bands and joins
    /// every worker. <paramref name="firstOrdinal"/> gives the serial ordinal of a row's first item, so a worker stops
    /// claiming once the next band starts above the failure cutoff. Returns the cutoff holding the lowest-ordinal
    /// failure, so the caller can add failures that keep a later logical position before throwing.</summary>
    internal ContactNavigationCutoff Run(ContactNavigationPhase phase, int rows, Func<int, long> firstOrdinal,
        Action<ContactNavigationProof, ContactNavigationBand> work)
    {
        var cutoff = new ContactNavigationCutoff();
        int bands = BandCount(Count, rows);
        if (bands <= 0) return cutoff;
        var schedule = new ContactNavigationSchedule(rows, bands, firstOrdinal, cutoff);
        int count = Math.Min(Count, bands);
        WorkerDiagnosticsRecord[]? records = _diagnostics.Enabled ? new WorkerDiagnosticsRecord[count] : null;
        var threads = new Thread[count];
        int started = 0;
        ThreadInterruptedException? interrupted = null;
        try
        {
            for (; started < count; started++)
            {
                int worker = started;
                var thread = new Thread(() => Work(phase, worker, schedule, cutoff, work, records))
                {
                    IsBackground = true,
                    Name = $"Contact navigation {phase} worker {worker}",
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
            // An interrupt stops the workers at their next item, but every one is still joined before it propagates,
            // so no binding is released while a worker can use it.
            for (int worker = 0; worker < started; worker++)
                while (true)
                {
                    try
                    {
                        // Raised inside the try, so a second interrupt while it waits on the cutoff retries the join.
                        if (interrupted is not null) cutoff.Fail(long.MinValue, interrupted);
                        threads[worker].Join();
                        break;
                    }
                    catch (ThreadInterruptedException interrupt)
                    {
                        interrupted ??= interrupt;
                    }
                }
        }
        if (records is not null) WorkerDiagnostics.Merge(records.AsSpan(0, started));
        if (interrupted is not null) ExceptionDispatchInfo.Capture(interrupted).Throw();
        return cutoff;
    }

    void Work(ContactNavigationPhase phase, int worker, ContactNavigationSchedule schedule,
        ContactNavigationCutoff cutoff, Action<ContactNavigationProof, ContactNavigationBand> work,
        WorkerDiagnosticsRecord[]? records)
    {
        try
        {
            var scope = WorkerDiagnosticsScope.Enter(_diagnostics, _observers[worker]);
            SupportReferenceEvaluation.Scope? reference = _reuse.ReferenceEvaluation ? SupportReferenceEvaluation.Begin() : null;
            try
            {
                ContactNavigationProof proof = _proofs[worker]!;
                while (schedule.TryClaim(out ContactNavigationBand? band))
                {
                    try
                    {
                        work(proof, band);
                        _hooks?.BandCompleted?.Invoke(phase, band.Index);
                    }
                    catch (Exception failure)
                    {
                        // Every later band starts above this failure, so the worker is done.
                        band.Fail(failure);
                        return;
                    }
                }
            }
            finally
            {
                try { reference?.Dispose(); }
                finally
                {
                    if (records is not null) records[worker] = scope.Exit();
                    else scope.Restore();
                }
            }
        }
        catch (Exception failure)
        {
            // A failure outside any band, in a claim or the diagnostics scope, has no serial position. It stops the
            // whole phase, and an unhandled exception on a worker thread would end the process.
            cutoff.Fail(long.MinValue, failure);
        }
    }

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

/// <summary>One phase's bands in row order. Workers claim them in ascending order through one counter, and a claim is
/// refused once the band's first item lies above the failure cutoff. Every later band lies above it too, so every
/// band below the final cutoff was claimed and ran.</summary>
internal sealed class ContactNavigationSchedule(int rows, int count, Func<int, long> firstOrdinal,
    ContactNavigationCutoff cutoff)
{
    int _next = -1;

    internal bool TryClaim([NotNullWhen(true)] out ContactNavigationBand? band)
    {
        band = null;
        int index = Interlocked.Increment(ref _next);
        if (index >= count) return false;
        int start = Start(index);
        long first = firstOrdinal(start);
        if (!cutoff.Admits(first)) return false;
        band = new ContactNavigationBand(index, start, Start(index + 1), first, cutoff);
        return true;
    }

    int Start(int index) => (int)((long)rows * index / count);
}

/// <summary>One band of contiguous rows in one phase. <see cref="Index"/> is its position in row order, independent of
/// the worker that claimed it. <see cref="Admits"/> runs between proofs and records the serial ordinal of the item
/// about to start, so a failure carries that ordinal.</summary>
internal sealed class ContactNavigationBand(int index, int start, int end, long firstOrdinal,
    ContactNavigationCutoff cutoff)
{
    long _current = firstOrdinal;

    internal int Index { get; } = index;
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
