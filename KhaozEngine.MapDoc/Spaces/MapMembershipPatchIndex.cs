using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Borrowed patch rows skip empty target-lattice ranges without visiting virtual cells.</summary>
internal sealed class MapMembershipPatchIndex
{
    readonly Dictionary<string, MapSurfacePatch[]> _patches;
    internal MapMembershipPatchIndex(MapScopedSurfaces view)
    {
        _patches = view.Witness.Present.Select(p => view.TryAcquiredPatch(p.Key, out var patch, out _) ? patch : null)
            .Where(p => p is not null).Select(p => p!).GroupBy(p => p.Key.SurfaceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Key.SlotZ).ThenBy(p => p.Key.SlotX).ToArray(), StringComparer.Ordinal);
    }
    internal IEnumerable<MapSurfacePatch> Overlapping(string surface, MapCellRect range)
    {
        if (!_patches.TryGetValue(surface, out var patches)) yield break;
        var first = MapPatchKey.ForCell(surface, range.MinX, range.MinZ);
        var last = MapPatchKey.ForCell(surface, checked(range.MaxXExclusive - 1), checked(range.MaxZExclusive - 1));
        int row = LowerZ(patches, first.SlotZ);
        while (row < patches.Length && patches[row].Key.SlotZ <= last.SlotZ)
        {
            int end = LowerZ(patches, checked(patches[row].Key.SlotZ + 1));
            int lo = row, hi = end;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (patches[mid].Key.SlotX < first.SlotX) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < end && patches[i].Key.SlotX <= last.SlotX; i++) yield return patches[i];
            row = end;
        }
    }
    static int LowerZ(MapSurfacePatch[] patches, long z)
    {
        int lo = 0, hi = patches.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (patches[mid].Key.SlotZ < z) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
