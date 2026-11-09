using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Shared parent indexes with one sorted, consumer-owned candidate list per convex piece.</summary>
internal sealed class MapPrismParentIndex : IDisposable
{
    sealed record Entry(int[] Parents, MapBoundsIndex Index, MapPreparationReservation? Ownership);
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly ILookup<string, int> _bySpace;
    readonly MapWaterPolygons.Bounds[] _bounds;
    internal MapPrismParentIndex(ILookup<string, int> bySpace, MapWaterPolygons.Bounds[] bounds)
    { _bySpace = bySpace; _bounds = bounds; }
    internal IEnumerable<int> Candidates(string owner, MapWaterPolygons.Bounds extent)
    {
        var admission = MapPrismValidationWorkspace.Admission;
        if (!_entries.TryGetValue(owner, out var entry))
        {
            int count = _bySpace[owner].Count();
            var ownership = admission?.Workspace();
            try
            {
                // Parent ids, exact bounds and the index's old/new node and order capacity.
                ownership?.Reserve(checked(512L + 512L * count));
                var parents = _bySpace[owner].ToArray();
                var index = new MapBoundsIndex(parents.Select(i => _bounds[i]).ToArray());
                _entries.Add(owner, entry = new(parents, index, ownership));
            }
            catch { ownership?.Dispose(); throw; }
        }
        var candidates = entry.Index.Overlapping(extent, admission: admission);
        MapPrismValidationWorkspace.Candidates(candidates.Count);
        foreach (int index in candidates) yield return entry.Parents[index];
    }
    public void Dispose() { foreach (var entry in _entries.Values) entry.Ownership?.Dispose(); }
}
