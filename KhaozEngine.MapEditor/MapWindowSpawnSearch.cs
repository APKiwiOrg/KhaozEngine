using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

internal static class MapWindowSpawnSearch
{
    static readonly IComparer<(double Distance, int Z, int X)> FurthestFirst =
        Comparer<(double Distance, int Z, int X)>.Create((left, right) => right.CompareTo(left));

    internal static MapTileEntry[] Nearest(IReadOnlyList<MapTileEntry> entries, MapTileCoord center, int count)
    {
        if (count == 0) return Array.Empty<MapTileEntry>();
        var nearest = new PriorityQueue<MapTileEntry, (double Distance, int Z, int X)>(
            Math.Min(count, entries.Count), FurthestFirst);
        foreach (MapTileEntry entry in entries)
        {
            var priority = (DistanceSquared(entry.Coord, center), entry.Coord.Z, entry.Coord.X);
            if (nearest.Count < count) nearest.Enqueue(entry, priority);
            else nearest.EnqueueDequeue(entry, priority);
        }
        // The heap evicts the furthest candidate. Only the retained read budget is sorted for probing.
        return nearest.UnorderedItems.OrderBy(item => item.Priority).Select(item => item.Element).ToArray();
    }

    static double DistanceSquared(MapTileCoord tile, MapTileCoord center)
    {
        double x = (double)tile.X - center.X, z = (double)tile.Z - center.Z;
        return x * x + z * z;
    }
}
