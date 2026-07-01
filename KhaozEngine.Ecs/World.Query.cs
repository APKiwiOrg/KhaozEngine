using KhaozEngine.Primitives;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Recycles the throwaway Query that each parameterless ForEach<...> overload would otherwise
    // allocate per call. The Query is rented, iterated, and returned all within the ForEach call, so
    // its lifetime is provably call-scoped (it never escapes). On Return the wrapper's Reset clears the
    // query for the next rent. Nested ForEach (a ForEach inside an action) is safe: when the single
    // pooled instance is already rented, Rent returns null and we fall back to a fresh Query, so each
    // nesting level uses a distinct instance and never aliases the outer iteration's matched set.
    internal sealed class PoolableQuery : IPoolable
    {
        public int PoolIndex { get; set; } = -1;
        public readonly Query Query;
        public PoolableQuery(Query query) => Query = query;
        public void Reset() => Query.ResetFilters();
    }

    // Initialized in the World constructor (factory captures `this`, unavailable in a field initializer).
    internal readonly ObjectPool<PoolableQuery> _forEachQueryPool;

    /// <summary>Starts a filtered query.</summary>
    public Query Query() => new(this);

    private Query RentForEachQuery(out PoolableQuery? rented)
    {
        // Renting a query is the shared entry for every ForEach/ParallelForEach. Guarding here rejects reentrant
        // iteration (a ForEach or nested ParallelForEach) from inside a parallel action. The outer ParallelForEach
        // rents BEFORE its parallel section starts, so it never trips its own guard.
        ThrowIfInParallelSection("ForEach");
        rented = _forEachQueryPool.Rent();
        return rented?.Query ?? new Query(this);   // pool exhausted (nested) -> fresh, un-pooled instance
    }

    private void ReturnForEachQuery(PoolableQuery? rented)
    {
        if (rented is not null) _forEachQueryPool.Return(rented);
    }

    public void ForEach<T1>(RefAction<T1> a) where T1 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2>(RefAction<T1, T2> a)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3>(RefAction<T1, T2, T3> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent
    {
        Query q = RentForEachQuery(out PoolableQuery? rented);
        try { q.ForEach(a); } finally { ReturnForEachQuery(rented); }
    }
}
