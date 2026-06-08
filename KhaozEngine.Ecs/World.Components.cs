using System;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    /// <summary>True if the entity currently has component <typeparamref name="T"/>.</summary>
    public bool Has<T>(Entity e) where T : struct, IComponent =>
        IsAlive(e) && _records[e.Id].Archetype.Has(Reg.Id<T>());

    /// <summary>Adds or overwrites component <typeparamref name="T"/> (adding triggers an archetype move).</summary>
    public void Set<T>(Entity e, T value) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        if (!_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: true);
        Record r = _records[e.Id];
        if (!Reg.IsTag(id))
            ((Column<T>)r.Archetype.Columns[id]).Set(r.Row, value);
    }

    /// <summary>Adds component <typeparamref name="T"/>; throws if already present.</summary>
    public void Add<T>(Entity e, T value) where T : struct, IComponent
    {
        if (_records[e.Id].Archetype.Has(Reg.Id<T>()))
            throw new InvalidOperationException($"Entity already has {typeof(T).Name}.");
        Set(e, value);
    }

    /// <summary>Removes component <typeparamref name="T"/> (no-op if absent).</summary>
    public void Remove<T>(Entity e) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        if (_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: false);
    }

    /// <summary>Returns a live ref to component <typeparamref name="T"/>. Throws if absent or a tag.</summary>
    public ref T Get<T>(Entity e) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        Record r = _records[e.Id];
        return ref ((Column<T>)r.Archetype.Columns[id]).Get(r.Row);
    }

    /// <summary>Copies out component <typeparamref name="T"/> if present.</summary>
    public bool TryGet<T>(Entity e, out T value) where T : struct, IComponent
    {
        if (Has<T>(e)) { value = Get<T>(e); return true; }
        value = default;
        return false;
    }

    // Moves an entity to the archetype with componentTypeId added/removed: allocate a row there,
    // copy shared columns, swap-remove from the old archetype, fix the backfilled record.
    private void MoveEntity(int id, int componentTypeId, bool add)
    {
        // Safe across the calls below: no callee here resizes _records (only Spawn does, and it is
        // never reached from MoveEntity), so this ref stays valid for the whole method.
        ref Record rec = ref _records[id];
        Archetype from = rec.Archetype;
        int[] newSig = add ? AddToSignature(from.TypeIds, componentTypeId)
                           : RemoveFromSignature(from.TypeIds, componentTypeId);
        Archetype to = GetOrCreateArchetype(newSig);

        int destRow = to.AddRow(new Entity(id, rec.Version));
        foreach (var kv in from.Columns)
            if (to.Columns.TryGetValue(kv.Key, out Column? destCol))
                kv.Value.CopyRow(destCol, rec.Row, destRow);

        int oldRow = rec.Row;
        if (from.SwapRemove(oldRow, out Entity moved))
            _records[moved.Id].Row = oldRow;

        rec.Archetype = to;
        rec.Row = destRow;
    }

    private Archetype GetOrCreateArchetype(int[] sortedSig)
    {
        var key = new ArchetypeSignature(sortedSig);
        if (!Archetypes.TryGetValue(key, out Archetype? a))
        {
            a = new Archetype(sortedSig, Reg);
            Archetypes[key] = a;
            ArchetypeGen++;
        }
        return a;
    }

    private static int[] AddToSignature(int[] sig, int id)
    {
        var r = new int[sig.Length + 1];
        int i = 0;
        while (i < sig.Length && sig[i] < id) { r[i] = sig[i]; i++; }
        r[i] = id;
        for (int j = i; j < sig.Length; j++) r[j + 1] = sig[j];
        return r;
    }

    private static int[] RemoveFromSignature(int[] sig, int id)
    {
        var r = new int[sig.Length - 1];
        int k = 0;
        foreach (int x in sig) if (x != id) r[k++] = x;
        return r;
    }
}
