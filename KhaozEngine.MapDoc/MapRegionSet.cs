using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The document's named regions in runtime form: each region's shape resolved to its
/// <see cref="IArea2D"/> once, with a point lookup both heads and the editor share. Built through
/// <see cref="MapRuntime.BuildRegions"/>. Regions with no shape are skipped, matching the scatter
/// builder. Document order is preserved, so the set is deterministic for a given document.</summary>
public sealed class MapRegionSet
{
    readonly MapRegion[] _regions;
    readonly IArea2D[] _areas;
    readonly float[] _centerX;
    readonly float[] _centerZ;

    MapRegionSet(MapRegion[] regions, IArea2D[] areas, float[] centerX, float[] centerZ)
    {
        _regions = regions;
        _areas = areas;
        _centerX = centerX;
        _centerZ = centerZ;
    }

    /// <summary>The shaped regions, in document order.</summary>
    public IReadOnlyList<MapRegion> Regions => _regions;

    /// <summary>The containing region nearest by shape center, or null when nothing contains the
    /// point. A shape with no derivable center scores distance zero, the editor's established
    /// tiebreak. The optional filter skips regions it rejects.</summary>
    public MapRegion? RegionAt(float x, float z, Func<MapRegion, bool>? filter = null)
    {
        MapRegion? best = null;
        float bestDist = 0f;
        for (int i = 0; i < _regions.Length; i++)
        {
            if (filter is not null && !filter(_regions[i])) continue;
            if (!_areas[i].Contains(x, z)) continue;
            float d = 0f;
            if (!float.IsNaN(_centerX[i]))
            {
                float dx = x - _centerX[i], dz = z - _centerZ[i];
                d = dx * dx + dz * dz;
            }
            if (best is null || d < bestDist) { best = _regions[i]; bestDist = d; }
        }
        return best;
    }

    // NaN marks a shape whose center could not be derived, so RegionAt scores it zero rather than guessing one.
    internal static MapRegionSet Build(MapDocument doc)
    {
        var regions = new List<MapRegion>();
        var areas = new List<IArea2D>();
        var cx = new List<float>();
        var cz = new List<float>();
        foreach (MapRegion region in doc.Regions)
        {
            if (region.Shape is null) continue;
            regions.Add(region);
            areas.Add(region.Shape.ToArea());
            bool centered = MapShapeGeometry.TryCenter(region.Shape, out float x, out float z);
            cx.Add(centered ? x : float.NaN);
            cz.Add(centered ? z : float.NaN);
        }
        return new MapRegionSet(regions.ToArray(), areas.ToArray(), cx.ToArray(), cz.ToArray());
    }
}
