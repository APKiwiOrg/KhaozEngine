using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

internal readonly record struct MapMembershipCellRange(MapMembershipGeometryArena Arena, int Start, int Length)
{ internal MapMembershipPreparedGeometry.Cell this[int index] => new(Arena, checked(Start + index)); }

/// <summary>Patch blocks replace a per-cell dictionary. Slots are authored in ascending order within a block.</summary>
internal sealed class MapMembershipCellLookup(MapMembershipGeometryArena arena, int capacity)
{
    readonly Dictionary<MapPatchKey, (int Start, int Count)> _patches = new(capacity);
    internal int PatchCount => _patches.Count;
    internal void Add(MapPatchKey patch, int start, int count) => _patches.Add(patch, (start, count));
    internal MapMembershipPreparedGeometry.Cell this[MapCellDemand demand]
    {
        get
        {
            var block = _patches[demand.Patch];
            int low = block.Start, high = block.Start + block.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int slot = arena.Cells[middle].Slot;
                if (slot == demand.SlotCell) return new(arena, middle);
                if (slot < demand.SlotCell) low = middle + 1; else high = middle - 1;
            }
            throw new KeyNotFoundException("Missing prepared membership cell.");
        }
    }
}

/// <summary>Cell face sets are ranges in their existing cell rows. Only opening sets need extra rows.</summary>
internal sealed class MapMembershipFaceSets(MapMembershipGeometryArena arena)
{
    readonly List<MapMembershipTriangleRange> _openings = new();
    internal int Count => checked(arena.Cells.Count + _openings.Count);
    internal long InventoryBytes => checked(64L + (_openings.Capacity == 0 ? 0 : 24L + 16L * _openings.Capacity));
    internal MapMembershipTriangleRange this[int index] => index < arena.Cells.Count
        ? new MapMembershipPreparedGeometry.Cell(arena, index).Triangles : _openings[index - arena.Cells.Count];
    internal int Add(MapMembershipTriangleRange faces)
    {
        if (_openings.Count == _openings.Capacity)
        {
            int capacity = _openings.Capacity == 0 ? 4 : checked(2 * _openings.Capacity);
            long old = _openings.Capacity == 0 ? 0 : 24L + 16L * _openings.Capacity;
            MapMembershipBuild.Inventory(checked(24L + 16L * capacity));
            _openings.Capacity = capacity;
            MapMembershipBuild.ReleaseInventory(old);
        }
        int index = Count; _openings.Add(faces); return index;
    }
}
