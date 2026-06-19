using System;
using System.Collections.Generic;
using KhaozEngine.Pooling;

namespace KhaozEngine.Ecs;

/// <summary>
/// An archetype entity-component-system world. Entities are versioned handles; components are
/// structs stored in archetype columns. See docs/USING for the contract.
/// </summary>
public sealed partial class World
{
    private struct Record { public Archetype Archetype; public int Row; public uint Version; public bool Alive; }

    private Record[] _records = new Record[64];
    private int _nextId;
    private readonly Stack<int> _free = new();

    internal readonly ComponentRegistry Reg = new();
    internal readonly Dictionary<ArchetypeSignature, Archetype> Archetypes = new();
    internal readonly List<Archetype> ArchetypeOrder = new();   // archetypes in creation order (deterministic iteration)
    internal int ArchetypeGen;
    private readonly Archetype _empty;

    public World()
    {
        _empty = new Archetype(Array.Empty<int>(), Reg);
        Archetypes[new ArchetypeSignature(Array.Empty<int>())] = _empty;
        ArchetypeOrder.Add(_empty);
        ArchetypeGen++;

        // Small prewarm covers a few levels of nested ForEach zero-alloc; deeper nesting falls back to
        // a fresh Query (see RentForEachQuery). Bound to this World so pooled queries match its archetypes.
        _forEachQueryPool = new ObjectPool<PoolableQuery>(() => new PoolableQuery(new Query(this)), prewarmCount: 4);
    }

    /// <summary>Creates a new entity with no components.</summary>
    public Entity Spawn()
    {
        int id = _free.Count > 0 ? _free.Pop() : _nextId++;
        if (id >= _records.Length) Array.Resize(ref _records, Math.Max(_records.Length * 2, id + 1));
        ref Record rec = ref _records[id];
        if (rec.Version == 0) rec.Version = 1;     // first use
        var e = new Entity(id, rec.Version);
        rec.Archetype = _empty;
        rec.Row = _empty.AddRow(e);
        rec.Alive = true;
        return e;
    }

    /// <summary>Removes an entity (no-op on a stale/dead handle). Bumps the slot version and recycles the id.</summary>
    public void Despawn(Entity e)
    {
        if (!IsAlive(e)) return;
        DetachHierarchyOnDespawn(e);
        ref Record rec = ref _records[e.Id];
        foreach (int tid in rec.Archetype.TypeIds)
            TrackRemoved(e, tid);
        if (rec.Archetype.SwapRemove(rec.Row, out Entity moved))
            _records[moved.Id].Row = rec.Row;
        rec.Alive = false;
        rec.Version++;
        _free.Push(e.Id);
    }

    /// <summary>True if the handle refers to a live entity (version still matches).</summary>
    public bool IsAlive(Entity e) =>
        (uint)e.Id < (uint)_records.Length && _records[e.Id].Alive && _records[e.Id].Version == e.Version;
}
