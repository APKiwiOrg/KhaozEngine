using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using KhaozEngine.Simulation;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Stateless, so one shared instance is safe across all worlds/threads. Default scheduler = inline, which makes
    // ParallelForEach identical to ForEach (sequential) until the caller passes a parallel scheduler. Opt-in.
    private static readonly IJobScheduler InlineScheduler = new SingleThreadedJobScheduler();

    // Unwraps the single-inner AggregateException a thread-pool scheduler wraps a worker exception in, so callers see
    // the original (e.g. ParallelAccessViolationException) directly. Only ever invoked from a catch whose `when`
    // filter has already checked InnerExceptions.Count == 1, so a multi-inner aggregate is never routed here and is
    // left to propagate unchanged. Marked DoesNotReturn because ExceptionDispatchInfo.Throw never returns normally.
    // The section bracket is inlined into each overload (rather than wrapped in a delegate) so a steady-state
    // ParallelForEach allocates no per-call closure.
    [DoesNotReturn]
    private static void RethrowSingleInner(AggregateException ae) =>
        ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();

    // ---- Pure data-parallel ForEach: mirrors ForEach<...>, fanning archetype row ranges across `scheduler`
    // (default inline = identical to ForEach). The action must be per-row-pure (touch only the ref components handed
    // in for the current entity). The hazard guard enforces this when ParallelHazardChecks is on. ----

    public void ParallelForEach<T1>(RefAction<T1> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2>(RefAction<T1, T2> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3>(RefAction<T1, T2, T3> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); }
    }

    // ---- Buffered data-parallel ForEach: each worker chunk records structural changes into its own
    // EntityCommandBuffer (rented from the World pool). After the parallel section the buffers are played back in
    // deterministic (row) order, so the result is identical to a sequential ForEach recording into a single buffer.
    // The sink list is pooled and each buffer is returned to the pool after its playback. On an exception in the
    // section the dirty buffers are dropped, never pooled (see DropSink / PlaybackSink). ----

    public void ParallelForEach<T1>(RefBufferAction<T1> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        List<EntityCommandBuffer> sink = RentEcbSink();
        bool sectionOk = false;
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler, sink); sectionOk = true; }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); if (!sectionOk) DropSink(sink); }
        if (sectionOk) PlaybackSink(sink);
    }

    public void ParallelForEach<T1, T2>(RefBufferAction<T1, T2> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        List<EntityCommandBuffer> sink = RentEcbSink();
        bool sectionOk = false;
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler, sink); sectionOk = true; }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); if (!sectionOk) DropSink(sink); }
        if (sectionOk) PlaybackSink(sink);
    }

    public void ParallelForEach<T1, T2, T3>(RefBufferAction<T1, T2, T3> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        List<EntityCommandBuffer> sink = RentEcbSink();
        bool sectionOk = false;
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler, sink); sectionOk = true; }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); if (!sectionOk) DropSink(sink); }
        if (sectionOk) PlaybackSink(sink);
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefBufferAction<T1, T2, T3, T4> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        List<EntityCommandBuffer> sink = RentEcbSink();
        bool sectionOk = false;
        BeginParallelSection();
        try { q.ParallelForEach(action, scheduler ?? InlineScheduler, sink); sectionOk = true; }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1) { RethrowSingleInner(ae); }
        finally { EndParallelSection(); ReturnForEachQuery(rented); if (!sectionOk) DropSink(sink); }
        if (sectionOk) PlaybackSink(sink);
    }
}
