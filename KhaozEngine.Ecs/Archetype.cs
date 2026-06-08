using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>Stores all entities sharing one component-type set. Each non-tag type has a column;
/// entities/components are addressed by row.</summary>
internal sealed class Archetype
{
    public readonly int[] TypeIds;                          // sorted signature
    public readonly Dictionary<int, Column> Columns = new();
    public Entity[] Entities = new Entity[8];
    public int Count;

    public Archetype(int[] sortedTypeIds, ComponentRegistry reg)
    {
        TypeIds = sortedTypeIds;
        foreach (int id in sortedTypeIds)
            if (!reg.IsTag(id))
                Columns[id] = reg.CreateColumn(id);
    }

    public bool Has(int typeId) => Array.BinarySearch(TypeIds, typeId) >= 0;

    public int AddRow(Entity e)
    {
        EnsureCapacity(Count + 1);
        int row = Count++;
        Entities[row] = e;
        return row;
    }

    /// <summary>Removes <paramref name="row"/> by moving the last row into it. Returns true and the
    /// backfilled entity when a move happened (its record's row must be updated to <paramref name="row"/>).</summary>
    public bool SwapRemove(int row, out Entity moved)
    {
        int last = --Count;
        if (row != last)
        {
            moved = Entities[last];
            Entities[row] = moved;
            foreach (Column col in Columns.Values) col.SwapRemove(row, last);
            return true;
        }
        moved = default;
        return false;
    }

    private void EnsureCapacity(int cap)
    {
        if (Entities.Length < cap)
        {
            int n = Entities.Length;
            while (n < cap) n *= 2;
            Array.Resize(ref Entities, n);
        }
        foreach (Column col in Columns.Values) col.EnsureCapacity(cap);
    }
}
