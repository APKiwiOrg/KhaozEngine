using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // --- save accessors (read-only) ---
    internal int SaveNextId => _nextId;
    internal IEnumerable<(int id, uint version)> SaveFreeSlots =>
        _free.Select(id => (id, _records[id].Version));
    internal IEnumerable<Archetype> SaveArchetypes => ArchetypeOrder;
    internal ComponentRegistry Registry => Reg;

    // --- load surface ---

    /// <summary>Places an entity at a specific id and version (bypasses the free-list). For load.</summary>
    internal Entity CreateAt(int id, uint version)
    {
        EnsureRecord(id);
        var e = new Entity(id, version);
        ref Record rec = ref _records[id];
        rec.Archetype = _empty;
        rec.Row = _empty.AddRow(e);
        rec.Version = version;
        rec.Alive = true;
        return e;
    }

    /// <summary>Adds (or overwrites) a component identified by runtime <see cref="Type"/>. For load.</summary>
    internal void SetByType(Entity e, Type type, object value)
    {
        int id = Reg.RegisterType(type);
        if (!_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: true);
        if (!Reg.IsTag(id))
        {
            Record r = _records[e.Id];
            r.Archetype.Columns[id].SetBoxed(r.Row, value);
        }
    }

    /// <summary>Restores the id allocator: next fresh id, and the recycled free slots (id + version).</summary>
    internal void RestoreAllocator(int nextId, IEnumerable<(int id, uint version)> freeSlots)
    {
        _nextId = nextId;
        _free.Clear();
        // Push so the first-listed slot is popped first (Spawn pops the top).
        var ordered = freeSlots.ToArray();
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            var (id, version) = ordered[i];
            EnsureRecord(id);
            _records[id].Version = version;
            _records[id].Alive = false;
            _free.Push(id);
        }
    }

    private void EnsureRecord(int id)
    {
        if (id >= _records.Length)
            Array.Resize(ref _records, Math.Max(_records.Length * 2, id + 1));
    }
}
