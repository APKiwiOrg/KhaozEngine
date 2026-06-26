using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using KhaozEngine.Simulation;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Stateless, so one shared instance is safe across all worlds/threads. Default scheduler = inline, which makes
    // ParallelForEach identical to ForEach (sequential) until the caller passes a parallel scheduler. Opt-in.
    private static readonly IJobScheduler InlineScheduler = new SingleThreadedJobScheduler();

    // Brackets a parallel section with the hazard guard and unwraps the AggregateException a thread-pool scheduler
    // wraps a worker exception in, so callers see the original (e.g. ParallelAccessViolationException) directly.
    private void RunParallel(Action body)
    {
        BeginParallelSection();
        try { body(); }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
        finally { EndParallelSection(); }
    }

    // ---- Pure data-parallel ForEach: mirrors ForEach<...>, fanning archetype row ranges across `scheduler`
    // (default inline = identical to ForEach). The action must be per-row-pure (touch only the ref components handed
    // in for the current entity); the hazard guard enforces this when ParallelHazardChecks is on. ----

    public void ParallelForEach<T1>(RefAction<T1> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2>(RefAction<T1, T2> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3>(RefAction<T1, T2, T3> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler)); }
        finally { ReturnForEachQuery(rented); }
    }

    // ---- Buffered data-parallel ForEach: each worker chunk records structural changes into its own
    // EntityCommandBuffer; after the parallel section the buffers are played back in deterministic (row) order, so
    // the result is identical to a sequential ForEach recording into a single buffer. ----

    public void ParallelForEach<T1>(RefBufferAction<T1> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        var sink = new List<EntityCommandBuffer>();
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler, sink)); }
        finally { ReturnForEachQuery(rented); }
        for (int i = 0; i < sink.Count; i++) sink[i].Playback(this);
    }

    public void ParallelForEach<T1, T2>(RefBufferAction<T1, T2> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        var sink = new List<EntityCommandBuffer>();
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler, sink)); }
        finally { ReturnForEachQuery(rented); }
        for (int i = 0; i < sink.Count; i++) sink[i].Playback(this);
    }

    public void ParallelForEach<T1, T2, T3>(RefBufferAction<T1, T2, T3> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        var sink = new List<EntityCommandBuffer>();
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler, sink)); }
        finally { ReturnForEachQuery(rented); }
        for (int i = 0; i < sink.Count; i++) sink[i].Playback(this);
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefBufferAction<T1, T2, T3, T4> action, IJobScheduler? scheduler = null)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(action);
        Query q = RentForEachQuery(out PoolableQuery? rented);
        var sink = new List<EntityCommandBuffer>();
        try { RunParallel(() => q.ParallelForEach(action, scheduler ?? InlineScheduler, sink)); }
        finally { ReturnForEachQuery(rented); }
        for (int i = 0; i < sink.Count; i++) sink[i].Playback(this);
    }
}
