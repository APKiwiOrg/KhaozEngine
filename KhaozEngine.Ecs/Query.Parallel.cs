using System;
using System.Collections.Generic;
using KhaozEngine.Simulation;

namespace KhaozEngine.Ecs;

// Data-parallel iteration: partition each matched archetype's row range [0, Count) into contiguous chunks and fan
// them across an IJobScheduler. Rows are independent memory (Column<T>.Data[r]), so a per-row-pure action on
// disjoint ranges is race-free and order-independent - the result is bit-identical to the sequential ForEach
// regardless of how the scheduler runs the chunks. The hazard guard (set by World.RunParallel) is active while
// these run, so a non-pure action that calls back into the world throws.
public sealed partial class Query
{
    // Chunk count depends only on the row count and core count (never the scheduler), so inline and threadpool
    // produce identical chunk boundaries. Chunks are contiguous and visited/merged in index order, so playback /
    // results match a sequential pass. Aim for a few chunks per core for load balancing.
    private static int ChunkCount(int n)
    {
        if (n <= 1) return n;                       // 0 -> no jobs, 1 -> a single job
        return Math.Min(n, Environment.ProcessorCount * 4);
    }

    public void ParallelForEach<T1>(RefAction<T1> action, IJobScheduler scheduler)
        where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r]);
            });
        }
    }

    public void ParallelForEach<T1, T2>(RefAction<T1, T2> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r]);
            });
        }
    }

    public void ParallelForEach<T1, T2, T3>(RefAction<T1, T2, T3> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r]);
            });
        }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r]);
            });
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            T5[] d5 = ((Column<T5>)a.Columns[id5]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r]);
            });
        }
    }

    public void ParallelForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action, IJobScheduler scheduler)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            T5[] d5 = ((Column<T5>)a.Columns[id5]).Data;
            T6[] d6 = ((Column<T6>)a.Columns[id6]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r]);
            });
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
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            T5[] d5 = ((Column<T5>)a.Columns[id5]).Data;
            T6[] d6 = ((Column<T6>)a.Columns[id6]).Data;
            T7[] d7 = ((Column<T7>)a.Columns[id7]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r], ref d7[r]);
            });
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
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7) || !a.Has(id8)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            T5[] d5 = ((Column<T5>)a.Columns[id5]).Data;
            T6[] d6 = ((Column<T6>)a.Columns[id6]).Data;
            T7[] d7 = ((Column<T7>)a.Columns[id7]).Data;
            T8[] d8 = ((Column<T8>)a.Columns[id8]).Data;
            int k = ChunkCount(n);
            scheduler.For(k, p =>
            {
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ref d5[r], ref d6[r], ref d7[r], ref d8[r]);
            });
        }
    }

    // ---- Buffered variants: each chunk gets its own EntityCommandBuffer; appended to `sink` in archetype-then-chunk
    // order so World plays them back in row order after the section = identical to a sequential ForEach + one ECB. ----

    public void ParallelForEach<T1>(RefBufferAction<T1> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            int k = ChunkCount(n);
            var ecbs = NewBuffers(k);
            scheduler.For(k, p =>
            {
                EntityCommandBuffer ecb = ecbs[p];
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ecb);
            });
            sink.AddRange(ecbs);
        }
    }

    public void ParallelForEach<T1, T2>(RefBufferAction<T1, T2> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            int k = ChunkCount(n);
            var ecbs = NewBuffers(k);
            scheduler.For(k, p =>
            {
                EntityCommandBuffer ecb = ecbs[p];
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ecb);
            });
            sink.AddRange(ecbs);
        }
    }

    public void ParallelForEach<T1, T2, T3>(RefBufferAction<T1, T2, T3> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            int k = ChunkCount(n);
            var ecbs = NewBuffers(k);
            scheduler.For(k, p =>
            {
                EntityCommandBuffer ecb = ecbs[p];
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ecb);
            });
            sink.AddRange(ecbs);
        }
    }

    public void ParallelForEach<T1, T2, T3, T4>(RefBufferAction<T1, T2, T3, T4> action, IJobScheduler scheduler, List<EntityCommandBuffer> sink)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            int n = a.Count; if (n == 0) continue;
            Entity[] ents = a.Entities;
            T1[] d1 = ((Column<T1>)a.Columns[id1]).Data;
            T2[] d2 = ((Column<T2>)a.Columns[id2]).Data;
            T3[] d3 = ((Column<T3>)a.Columns[id3]).Data;
            T4[] d4 = ((Column<T4>)a.Columns[id4]).Data;
            int k = ChunkCount(n);
            var ecbs = NewBuffers(k);
            scheduler.For(k, p =>
            {
                EntityCommandBuffer ecb = ecbs[p];
                int start = (int)((long)p * n / k), end = (int)((long)(p + 1) * n / k);
                for (int r = start; r < end; r++) action(ents[r], ref d1[r], ref d2[r], ref d3[r], ref d4[r], ecb);
            });
            sink.AddRange(ecbs);
        }
    }

    private static EntityCommandBuffer[] NewBuffers(int k)
    {
        var ecbs = new EntityCommandBuffer[k];
        for (int i = 0; i < k; i++) ecbs[i] = new EntityCommandBuffer();
        return ecbs;
    }
}
