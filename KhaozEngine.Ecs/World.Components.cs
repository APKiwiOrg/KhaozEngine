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
        ThrowIfInParallelSection(nameof(Set));
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        int id = Reg.Id<T>();
        bool adding = !_records[e.Id].Archetype.Has(id);
        if (adding)
            MoveEntity(e.Id, id, add: true);
        Record r = _records[e.Id];
        if (!Reg.IsTag(id))
            ((Column<T>)r.Archetype.Columns[id]).Set(r.Row, value);
        TrackAddedOrChanged(e, id, adding);
    }

    /// <summary>Adds component <typeparamref name="T"/>; throws if already present.</summary>
    public void Add<T>(Entity e, T value) where T : struct, IComponent
    {
        ThrowIfInParallelSection(nameof(Add));
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        if (_records[e.Id].Archetype.Has(Reg.Id<T>()))
            throw new InvalidOperationException($"Entity already has {typeof(T).Name}.");
        Set(e, value);
    }

    /// <summary>Removes component <typeparamref name="T"/> (no-op if absent).</summary>
    public void Remove<T>(Entity e) where T : struct, IComponent
    {
        ThrowIfInParallelSection(nameof(Remove));
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        int id = Reg.Id<T>();
        if (_records[e.Id].Archetype.Has(id))
        {
            MoveEntity(e.Id, id, add: false);
            TrackRemoved(e, id);
        }
    }

    /// <summary>Returns a live ref to component <typeparamref name="T"/>. Throws if absent or a tag.</summary>
    public ref T Get<T>(Entity e) where T : struct, IComponent
    {
        ThrowIfInParallelSection(nameof(Get));
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        int id = Reg.Id<T>();
        Record r = _records[e.Id];
        return ref ((Column<T>)r.Archetype.Columns[id]).Get(r.Row);
    }

    /// <summary>Copies out component <typeparamref name="T"/> if present. A present zero-field tag copies out
    /// <c>default</c> (its only value), unlike <see cref="Get{T}"/> which throws for a tag (no column to ref into).</summary>
    public bool TryGet<T>(Entity e, out T value) where T : struct, IComponent
    {
        ThrowIfInParallelSection(nameof(TryGet));
        int id = Reg.Id<T>();
        if (!IsAlive(e) || !_records[e.Id].Archetype.Has(id))
        {
            value = default;
            return false;
        }
        // A tag has no column to read from: presence IS the whole state, so its value is always default.
        // Reading Columns[id] here would look up the missing column and crash.
        if (Reg.IsTag(id))
        {
            value = default;
            return true;
        }
        Record r = _records[e.Id];
        value = ((Column<T>)r.Archetype.Columns[id]).Get(r.Row);
        return true;
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

        // Rows moved in two archetypes, so any serial iteration in flight is now walking a stale row range.
        // Counted here rather than in Set/Remove so a future caller of MoveEntity cannot forget it.
        MarkStructuralChange(add ? "Set/Add" : nameof(Remove));
    }

    private Archetype GetOrCreateArchetype(int[] sortedSig)
    {
        var key = new ArchetypeSignature(sortedSig);
        if (!Archetypes.TryGetValue(key, out Archetype? a))
        {
            a = new Archetype(sortedSig, Reg);
            Archetypes[key] = a;
            ArchetypeOrder.Add(a);
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
