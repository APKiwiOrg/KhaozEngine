using System;
using System.Collections.Generic;
using KhaozEngine.Simulation;

namespace KhaozEngine.Ecs;

// Data-parallel iteration: partition each matched archetype's row range [0, Count) into contiguous chunks and fan
// them across an IJobScheduler. Rows are independent memory (Column<T>.Data[r]), so a per-row-pure action on
// disjoint ranges is race-free and order-independent - the result is bit-identical to the sequential ForEach
// regardless of how the scheduler runs the chunks. The hazard guard (set by World's parallel-section bracket) is
// active while these run, so a non-pure action that calls back into the world throws.
//
// Allocation: the per-chunk state (entity array, column arrays, row count, chunk count, action, and for the
// buffered variants the command buffers) is hoisted into a small per-arity context object cached on this Query,
// and the Action<int> the scheduler drives is that context's Body delegate, built once. A steady-state
// ParallelForEach therefore allocates neither a closure nor a delegate per archetype. The context is only ever
// touched by one iteration at a time: its fields are written on the calling thread before scheduler.For and read
// (never written) by the worker chunks during it, and the scheduler's fan-out/join provide the memory barriers,
// so the concurrent reads are safe. Reuse is safe because a Query is rented per outer ForEach/ParallelForEach and
// re-entrant iteration is rejected by the hazard guard (or, with checks off, falls back to a distinct pooled/fresh
// Query), so a single context per arity per Query is never aliased by two live iterations. The cache is
// monomorphic: it holds one instantiation per arity, so if the SAME Query alternates between two different generic
// argument sets at the same arity the context is re-created on each switch (steady-state single-shape loops, which
// the allocation tests pin, stay alloc-free).
public sealed partial class Query
{
    // Cached per-arity chunk contexts (one field per non-buffered arity T1..T8 and buffered arity T1..T4). Held as
    // object because the context is generic over the component types but this Query is not. Each call casts back to
    // its concrete type and rebuilds only on a cache miss (first use or a different generic shape).
    private object? _pfe1, _pfe2, _pfe3, _pfe4, _pfe5, _pfe6, _pfe7, _pfe8;
    private object? _bpfe1, _bpfe2, _bpfe3, _bpfe4;

    // Reused staging array for the buffers rented per archetype on the buffered path, grown on demand. The buffers
    // it holds are copied into the caller's sink each archetype, so the array itself is pure scratch.
    private EntityCommandBuffer[] _ecbScratch = Array.Empty<EntityCommandBuffer>();

    // Chunk count depends only on the row count and core count (never the scheduler), so inline and threadpool
    // produce identical chunk boundaries. Chunks are contiguous and visited/merged in index order, so playback /
    // results match a sequential pass. Aim for a few chunks per core for load balancing.
    private static int ChunkCount(int n)
    {
        if (n <= 1) return n;                       // 0 -> no jobs, 1 -> a single job
        return Math.Min(n, Environment.ProcessorCount * 4);
    }

    // Rents k command buffers from the World pool into the reused scratch array (grown on demand). Replaces the old
    // NewBuffers, which allocated a fresh EntityCommandBuffer[k] plus k new buffers per archetype per call. The
    // buffers are appended to the sink by the caller and returned to the World pool after playback, so a
    // steady-state buffered pass allocates nothing here.
    private EntityCommandBuffer[] RentBuffers(int k)
    {
        if (_ecbScratch.Length < k) _ecbScratch = new EntityCommandBuffer[k];
        for (int i = 0; i < k; i++) _ecbScratch[i] = _world.RentEcb();
        return _ecbScratch;
    }

    public void ParallelForEach<T1>(RefAction<T1> action, IJobScheduler scheduler)
        where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        if (_pfe1 is not ForEachChunk<T1> ctx) { ctx = new ForEachChunk<T1>(); _pfe1 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2>(RefAction<T1, T2> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        if (_pfe2 is not ForEachChunk<T1, T2> ctx) { ctx = new ForEachChunk<T1, T2>(); _pfe2 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3>(RefAction<T1, T2, T3> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        if (_pfe3 is not ForEachChunk<T1, T2, T3> ctx) { ctx = new ForEachChunk<T1, T2, T3>(); _pfe3 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        if (_pfe4 is not ForEachChunk<T1, T2, T3, T4> ctx) { ctx = new ForEachChunk<T1, T2, T3, T4>(); _pfe4 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>();
        Refresh();
        if (_pfe5 is not ForEachChunk<T1, T2, T3, T4, T5> ctx) { ctx = new ForEachChunk<T1, T2, T3, T4, T5>(); _pfe5 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.D5 = ((Column<T5>)a.Columns[id5]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>();
        Refresh();
        if (_pfe6 is not ForEachChunk<T1, T2, T3, T4, T5, T6> ctx) { ctx = new ForEachChunk<T1, T2, T3, T4, T5, T6>(); _pfe6 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.D5 = ((Column<T5>)a.Columns[id5]).Data;
            ctx.D6 = ((Column<T6>)a.Columns[id6]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>(), id7 = _world.Reg.Id<T7>();
        Refresh();
        if (_pfe7 is not ForEachChunk<T1, T2, T3, T4, T5, T6, T7> ctx) { ctx = new ForEachChunk<T1, T2, T3, T4, T5, T6, T7>(); _pfe7 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.D5 = ((Column<T5>)a.Columns[id5]).Data;
            ctx.D6 = ((Column<T6>)a.Columns[id6]).Data;
            ctx.D7 = ((Column<T7>)a.Columns[id7]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>(), id7 = _world.Reg.Id<T7>(), id8 = _world.Reg.Id<T8>();
        Refresh();
        if (_pfe8 is not ForEachChunk<T1, T2, T3, T4, T5, T6, T7, T8> ctx) { ctx = new ForEachChunk<T1, T2, T3, T4, T5, T6, T7, T8>(); _pfe8 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7) || !a.Has(id8)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.D5 = ((Column<T5>)a.Columns[id5]).Data;
            ctx.D6 = ((Column<T6>)a.Columns[id6]).Data;
            ctx.D7 = ((Column<T7>)a.Columns[id7]).Data;
            ctx.D8 = ((Column<T8>)a.Columns[id8]).Data;
            ctx.N = n; ctx.K = ChunkCount(n);
            scheduler.For(ctx.K, ctx.Body);
        }
    }

    // ---- Buffered variants: each chunk gets its own EntityCommandBuffer (rented from the World pool). The buffers
    // are appended to `sink` in archetype-then-chunk order so World plays them back in row order after the section =
    // identical to a sequential ForEach + one ECB. World returns each buffer to the pool after playback. A caller
    // that supplies its own `sink` (the public overload) and never routes it through World's playback simply leaves
    // those buffers unpooled - the GC reclaims them, there is no leak. ----

    public void ParallelForEach<T1>(RefBufferAction<T1> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        if (_bpfe1 is not BufferedForEachChunk<T1> ctx) { ctx = new BufferedForEachChunk<T1>(); _bpfe1 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.N = n; int k = ChunkCount(n); ctx.K = k;
            EntityCommandBuffer[] ecbs = RentBuffers(k); ctx.Ecbs = ecbs;
            scheduler.For(k, ctx.Body);
            for (int i = 0; i < k; i++) sink.Add(ecbs[i]);
        }
    }

    public void ParallelForEach<T1, T2>(RefBufferAction<T1, T2> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        if (_bpfe2 is not BufferedForEachChunk<T1, T2> ctx) { ctx = new BufferedForEachChunk<T1, T2>(); _bpfe2 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.N = n; int k = ChunkCount(n); ctx.K = k;
            EntityCommandBuffer[] ecbs = RentBuffers(k); ctx.Ecbs = ecbs;
            scheduler.For(k, ctx.Body);
            for (int i = 0; i < k; i++) sink.Add(ecbs[i]);
        }
    }

    public void ParallelForEach<T1, T2, T3>(RefBufferAction<T1, T2, T3> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        if (_bpfe3 is not BufferedForEachChunk<T1, T2, T3> ctx) { ctx = new BufferedForEachChunk<T1, T2, T3>(); _bpfe3 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.N = n; int k = ChunkCount(n); ctx.K = k;
            EntityCommandBuffer[] ecbs = RentBuffers(k); ctx.Ecbs = ecbs;
            scheduler.For(k, ctx.Body);
            for (int i = 0; i < k; i++) sink.Add(ecbs[i]);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefBufferAction<T1, T2, T3, T4> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        if (_bpfe4 is not BufferedForEachChunk<T1, T2, T3, T4> ctx) { ctx = new BufferedForEachChunk<T1, T2, T3, T4>(); _bpfe4 = ctx; }
        ctx.Action = action;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            int n = a.Count; if (n == 0) continue;
            ctx.Ents = a.Entities;
            ctx.D1 = ((Column<T1>)a.Columns[id1]).Data;
            ctx.D2 = ((Column<T2>)a.Columns[id2]).Data;
            ctx.D3 = ((Column<T3>)a.Columns[id3]).Data;
            ctx.D4 = ((Column<T4>)a.Columns[id4]).Data;
            ctx.N = n; int k = ChunkCount(n); ctx.K = k;
            EntityCommandBuffer[] ecbs = RentBuffers(k); ctx.Ecbs = ecbs;
            scheduler.For(k, ctx.Body);
            for (int i = 0; i < k; i++) sink.Add(ecbs[i]);
        }
    }

    // ---- Per-arity chunk contexts. Each holds the current archetype's arrays plus row/chunk counts, and exposes a
    // single cached Body delegate that the scheduler drives. Start/end are computed from p exactly as the original
    // per-chunk closures did, so chunk boundaries and results are unchanged. Fields are copied into locals in Run so
    // the hot loop reads them once. ----

    private sealed class ForEachChunk<T1>
    {
        public RefAction<T1> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1> action = Action; Entity[] ents = Ents; T1[] d1 = D1;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2>
    {
        public RefAction<T1, T2> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2> action = Action; Entity[] ents = Ents; T1[] d1 = D1; T2[] d2 = D2;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3>
    {
        public RefAction<T1, T2, T3> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3> action = Action; Entity[] ents = Ents; T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3, T4>
    {
        public RefAction<T1, T2, T3, T4> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3, T4> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3, T4, T5>
    {
        public RefAction<T1, T2, T3, T4, T5> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public T5[] D5 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3, T4, T5> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4; T5[] d5 = D5;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3, T4, T5, T6>
    {
        public RefAction<T1, T2, T3, T4, T5, T6> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public T5[] D5 = null!;
        public T6[] D6 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3, T4, T5, T6> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4; T5[] d5 = D5; T6[] d6 = D6;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3, T4, T5, T6, T7>
    {
        public RefAction<T1, T2, T3, T4, T5, T6, T7> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public T5[] D5 = null!;
        public T6[] D6 = null!;
        public T7[] D7 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3, T4, T5, T6, T7> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4; T5[] d5 = D5; T6[] d6 = D6; T7[] d7 = D7;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r], ref d7[r]);
        }
    }

    private sealed class ForEachChunk<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public RefAction<T1, T2, T3, T4, T5, T6, T7, T8> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public T5[] D5 = null!;
        public T6[] D6 = null!;
        public T7[] D7 = null!;
        public T8[] D8 = null!;
        public int N, K;
        public readonly Action<int> Body;
        public ForEachChunk() => Body = Run;
        private void Run(int p)
        {
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefAction<T1, T2, T3, T4, T5, T6, T7, T8> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4; T5[] d5 = D5; T6[] d6 = D6; T7[] d7 = D7; T8[] d8 = D8;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r], ref d7[r], ref d8[r]);
        }
    }

    private sealed class BufferedForEachChunk<T1>
    {
        public RefBufferAction<T1> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public EntityCommandBuffer[] Ecbs = null!;
        public int N, K;
        public readonly Action<int> Body;
        public BufferedForEachChunk() => Body = Run;
        private void Run(int p)
        {
            EntityCommandBuffer ecb = Ecbs[p];
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefBufferAction<T1> action = Action; Entity[] ents = Ents; T1[] d1 = D1;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ecb);
        }
    }

    private sealed class BufferedForEachChunk<T1, T2>
    {
        public RefBufferAction<T1, T2> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public EntityCommandBuffer[] Ecbs = null!;
        public int N, K;
        public readonly Action<int> Body;
        public BufferedForEachChunk() => Body = Run;
        private void Run(int p)
        {
            EntityCommandBuffer ecb = Ecbs[p];
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefBufferAction<T1, T2> action = Action; Entity[] ents = Ents; T1[] d1 = D1; T2[] d2 = D2;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ecb);
        }
    }

    private sealed class BufferedForEachChunk<T1, T2, T3>
    {
        public RefBufferAction<T1, T2, T3> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public EntityCommandBuffer[] Ecbs = null!;
        public int N, K;
        public readonly Action<int> Body;
        public BufferedForEachChunk() => Body = Run;
        private void Run(int p)
        {
            EntityCommandBuffer ecb = Ecbs[p];
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefBufferAction<T1, T2, T3> action = Action; Entity[] ents = Ents; T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ecb);
        }
    }

    private sealed class BufferedForEachChunk<T1, T2, T3, T4>
    {
        public RefBufferAction<T1, T2, T3, T4> Action = null!;
        public Entity[] Ents = null!;
        public T1[] D1 = null!;
        public T2[] D2 = null!;
        public T3[] D3 = null!;
        public T4[] D4 = null!;
        public EntityCommandBuffer[] Ecbs = null!;
        public int N, K;
        public readonly Action<int> Body;
        public BufferedForEachChunk() => Body = Run;
        private void Run(int p)
        {
            EntityCommandBuffer ecb = Ecbs[p];
            int n = N, k = K, start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
            RefBufferAction<T1, T2, T3, T4> action = Action; Entity[] ents = Ents;
            T1[] d1 = D1; T2[] d2 = D2; T3[] d3 = D3; T4[] d4 = D4;
            for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ecb);
        }
    }
}
