using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Ecs;

/// <summary>A reusable query: With/Without filters over archetypes, iterated by ForEach (ref) or Entities().</summary>
public sealed partial class Query
{
    private readonly World _world;
    private readonly List<int> _with = new();
    private readonly List<int> _without = new();
    private readonly List<Archetype> _matched = new();
    private int _gen = -1;

    internal Query(World world) => _world = world;

    public Query With<T>() where T : struct, IComponent { _with.Add(_world.Reg.Id<T>()); return this; }
    public Query Without<T>() where T : struct, IComponent { _without.Add(_world.Reg.Id<T>()); return this; }

    /// <summary>Resets this query to its as-constructed state (no filters, match cache invalidated) so a
    /// pooled instance can be safely reused. Internal: only the <see cref="World"/> ForEach query pool
    /// recycles Query instances; user-built queries (via <see cref="World.Query"/>) are never pooled.</summary>
    internal void ResetFilters()
    {
        _with.Clear();
        _without.Clear();
        _gen = -1;          // force Refresh to rebuild _matched on next use
    }

    private void Refresh()
    {
        if (_gen == _world.ArchetypeGen) return;
        _gen = _world.ArchetypeGen;
        _matched.Clear();
        foreach (Archetype a in _world.ArchetypeOrder)
        {
            bool ok = true;
            foreach (int w in _with) if (!a.Has(w)) { ok = false; break; }
            if (ok) foreach (int wo in _without) if (a.Has(wo)) { ok = false; break; }
            if (ok) _matched.Add(a);
        }
    }

    // The iteration guard (#118). Iteration walks each archetype's rows by index, so a structural change made from
    // inside it swap-removes rows underneath the walk: one entity is visited twice, another is skipped for the rest
    // of the pass, and a change that GROWS the archetype resizes the column arrays the in-flight action's `ref`
    // parameters point into, so its later writes to them land in a detached array. The doc has always forbidden
    // this and nothing enforced it, which made the same misuse a loud ParallelAccessViolationException in parallel
    // code and silent corruption in serial code. So every iteration entry point snapshots the world's structural
    // version and rechecks it around each callback, and a mismatch throws at the offending row.
    //
    // Cost is one int compare per row, against a delegate invocation per row. It is deliberately NOT switchable:
    // ParallelHazardChecks earns its off switch by guarding every world call including reads, while this reads one
    // field, and the thing it prevents is silent data corruption rather than a diagnosable crash.
    private void ThrowIfStructurallyChanged(int expected)
    {
        if (_world.StructuralVersion != expected) ThrowStructuralChange(_world.LastStructuralOp);
    }

    // Kept out of line so the check itself stays a bare compare in the caller's loop.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStructuralChange(string operation) =>
        throw new StructuralChangeDuringIterationException(operation);

    /// <summary>Yields each matching entity (no component refs).</summary>
    /// <remarks>Structural changes (Spawn/Despawn/Add/Remove) made directly from the loop body throw
    /// <see cref="StructuralChangeDuringIterationException"/>: record them in <see cref="World.Commands"/> (or an
    /// <see cref="EntityCommandBuffer"/>) and play back afterward, or collect the entities and act after the loop.</remarks>
    public IEnumerable<Entity> Entities()
    {
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
            // The check follows the yield, exactly as ForEach checks after each action, and the order is
            // load-bearing rather than stylistic: the bound is the LIVE a.Count, so a despawn from the loop body
            // shrinks it. Checking BEFORE the yield lets that shrink end the loop first, and a two-entity pass
            // that despawns as it goes then returns normally having silently skipped the second entity, which is
            // the corruption this guard exists to refuse.
            for (int r = 0; r < a.Count; r++)
            {
                yield return a.Entities[r];
                ThrowIfStructurallyChanged(version);
            }
    }

    /// <remarks>Structural changes (Spawn/Despawn/Add/Remove) made directly inside the action throw
    /// <see cref="StructuralChangeDuringIterationException"/>: record them in <see cref="World.Commands"/> (or an
    /// <see cref="EntityCommandBuffer"/>) and play back afterward. Reading and writing components (the ref
    /// parameters, or Has/Get/TryGet/an overwriting Set through the world) stays legal.</remarks>
    public void ForEach<T1>(RefAction<T1> action) where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2>(RefAction<T1, T2> action)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3>(RefAction<T1, T2, T3> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            var c5 = (Column<T5>)a.Columns[id5];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            var c5 = (Column<T5>)a.Columns[id5];
            var c6 = (Column<T6>)a.Columns[id6];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>(), id7 = _world.Reg.Id<T7>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            var c5 = (Column<T5>)a.Columns[id5];
            var c6 = (Column<T6>)a.Columns[id6];
            var c7 = (Column<T7>)a.Columns[id7];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r], ref c7.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>(), id7 = _world.Reg.Id<T7>(), id8 = _world.Reg.Id<T8>();
        Refresh();
        int version = _world.StructuralVersion;
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5) || !a.Has(id6) || !a.Has(id7) || !a.Has(id8)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            var c5 = (Column<T5>)a.Columns[id5];
            var c6 = (Column<T6>)a.Columns[id6];
            var c7 = (Column<T7>)a.Columns[id7];
            var c8 = (Column<T8>)a.Columns[id8];
            int n = a.Count;
            for (int r = 0; r < n; r++)
            {
                action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r], ref c7.Data[r], ref c8.Data[r]);
                ThrowIfStructurallyChanged(version);
            }
        }
    }
}
