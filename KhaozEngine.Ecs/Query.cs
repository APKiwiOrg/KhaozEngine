using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A reusable query: With/Without filters over archetypes, iterated by ForEach (ref) or Entities().</summary>
public sealed class Query
{
    private readonly World _world;
    private readonly List<int> _with = new();
    private readonly List<int> _without = new();
    private readonly List<Archetype> _matched = new();
    private int _gen = -1;

    internal Query(World world) => _world = world;

    public Query With<T>() where T : struct, IComponent { _with.Add(_world.Reg.Id<T>()); return this; }
    public Query Without<T>() where T : struct, IComponent { _without.Add(_world.Reg.Id<T>()); return this; }

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

    /// <summary>Yields each matching entity (no component refs).</summary>
    /// <remarks>Do not make structural changes (Spawn/Despawn/Add/Remove) directly while iterating; record
    /// them in <see cref="World.Commands"/> (or an <see cref="EntityCommandBuffer"/>) and play back afterward.</remarks>
    public IEnumerable<Entity> Entities()
    {
        Refresh();
        foreach (Archetype a in _matched)
            for (int r = 0; r < a.Count; r++)
                yield return a.Entities[r];
    }

    /// <remarks>Do not make structural changes (Spawn/Despawn/Add/Remove) directly inside the action; record
    /// them in <see cref="World.Commands"/> (or an <see cref="EntityCommandBuffer"/>) and play back afterward.</remarks>
    public void ForEach<T1>(RefAction<T1> action) where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r]);
        }
    }

    public void ForEach<T1, T2>(RefAction<T1, T2> action)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r]);
        }
    }

    public void ForEach<T1, T2, T3>(RefAction<T1, T2, T3> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r]);
        }
    }

    public void ForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r]);
        }
    }

    public void ForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2) || !a.Has(id3) || !a.Has(id4) || !a.Has(id5)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            var c3 = (Column<T3>)a.Columns[id3];
            var c4 = (Column<T4>)a.Columns[id4];
            var c5 = (Column<T5>)a.Columns[id5];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r]);
        }
    }

    public void ForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> action)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>(), id3 = _world.Reg.Id<T3>(), id4 = _world.Reg.Id<T4>(),
            id5 = _world.Reg.Id<T5>(), id6 = _world.Reg.Id<T6>();
        Refresh();
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
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r]);
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
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r], ref c7.Data[r]);
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
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r], ref c3.Data[r], ref c4.Data[r], ref c5.Data[r], ref c6.Data[r], ref c7.Data[r], ref c8.Data[r]);
        }
    }
}
