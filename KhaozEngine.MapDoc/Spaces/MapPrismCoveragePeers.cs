using System.Collections.Generic;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>One bounded sorted peer row at a time, in the released lexicographic pair order.</summary>
internal static class MapPrismCoveragePeers
{
    internal static void Validate(MapScopedSurfaces view, IReadOnlyList<MapOccupiedPiece> raw,
        MapWaterPolygons.Bounds[] bounds, MapWaterWorkBudget work, List<string> errors)
    {
        var admission = MapPrismValidationWorkspace.Admission;
        using var indexWorkspace = admission?.BeginPreparation();
        admission?.ReserveBytes(checked(512L + 512L * raw.Count));
        var index = new MapBoundsIndex(bounds);
        for (int i = 0; i < raw.Count; i++)
        {
            using var row = admission?.BeginPreparation();
            var candidates = index.Overlapping(bounds[i], admission: admission);
            MapPrismValidationWorkspace.Candidates(candidates.Count);
            foreach (int j in candidates)
            {
                if (j <= i) continue;
                var a = raw[i]; var b = raw[j];
                if (a.SpaceId == b.SpaceId || MapOccupiedPartitions.Ancestor(view, a.SpaceId, b.SpaceId) || MapOccupiedPartitions.Ancestor(view, b.SpaceId, a.SpaceId)) continue;
                work.Charge(1);
                using var clipping = MapPrismValidationWorkspace.Kernel(a.Polygon.Count + b.Polygon.Count);
                var polygon = MapOccupiedPartitions.Intersect(a.Polygon, b.Polygon);
                if (!MapOccupiedPartitions.Positive(polygon)) continue;
                if (a.UpperFaces is not null || a.UpperPlane is not null)
                    polygon = MapOccupiedPartitions.Clip(polygon, p => MapOccupiedPartitions.Height(a, p, true).Subtract(MapOccupiedPartitions.Height(b, p, false)));
                if (b.UpperFaces is not null || b.UpperPlane is not null)
                    polygon = MapOccupiedPartitions.Clip(polygon, p => MapOccupiedPartitions.Height(b, p, true).Subtract(MapOccupiedPartitions.Height(a, p, false)));
                if (MapOccupiedPartitions.Positive(polygon) &&
                    System.Linq.Enumerable.Any(polygon, p => a.Top(p) is not { } ta || ta.CompareTo(b.Bottom(p)) > 0) &&
                    System.Linq.Enumerable.Any(polygon, p => b.Top(p) is not { } tb || tb.CompareTo(a.Bottom(p)) > 0))
                    errors.Add($"peer occupied overlap: '{a.SpaceId}' with '{b.SpaceId}'");
            }
        }
    }
}
