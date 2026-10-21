using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Render-only complements of the released disjoint paint regions, plus their true feather boundaries.
/// Shared canonical diagonals and collinear subdivisions cancel before any distance is sampled.</summary>
internal static class MapTerrainPaint
{
    internal readonly record struct FeatherKey(string Owner, string? Paint, ushort Underlay, ushort Overlay);
    internal readonly record struct Edge(MapWideXz A, MapWideXz B);
    readonly record struct Line(bool Vertical, MapWideRational Slope, MapWideRational Intercept);
    internal static FeatherKey Key(MapTerrainPartition p) => new(p.Face.OwnerId, p.PaintId, p.Paint.Underlay, p.Paint.Overlay);

    internal static IEnumerable<MapTerrainPartition> Partition(MapTerrainCanonicalFace face,
        IEnumerable<MapPaintRegion> published, MapSceneAdmission admission)
    {
        if (face.Face.Role == MapFaceRole.Wall)
        {
            yield return Create(null, face.Paint, new[] { face.Triangle.A, face.Triangle.B, face.Triangle.C });
            yield break;
        }
        var triangle = face.Triangle;
        var parent = MapWideRefinementGeometry.Normalize(new[] { MapWidePoint.From(triangle.A).Xz,
            MapWidePoint.From(triangle.B).Xz, MapWidePoint.From(triangle.C).Xz });
        var remaining = new List<IReadOnlyList<MapWideXz>> { parent };
        foreach (var region in published)
        {
            var clip = MapWideRefinementGeometry.Normalize(region.Polygon.Select(p => MapWidePoint.From(p).Xz));
            var next = new List<IReadOnlyList<MapWideXz>>();
            foreach (var polygon in remaining)
            {
                var inside = polygon;
                for (int i = 0; i < clip.Count && inside.Count >= 3; i++)
                {
                    var outside = MapEffectiveGeometry.ClipHalfPlane(inside, clip[i], clip[(i + 1) % clip.Count], inside: false);
                    if (MapEffectiveGeometry.AreaWide(outside).Sign != 0)
                    {
                        admission.ReserveBytes(checked(128 + 64L * outside.Count));
                        next.Add(MapWideRefinementGeometry.Normalize(outside));
                    }
                    inside = MapEffectiveGeometry.ClipHalfPlane(inside, clip[i], clip[(i + 1) % clip.Count]);
                }
            }
            remaining = next;
            yield return Create(region.PaintId, region.Material, region.Polygon);
        }
        foreach (var polygon in remaining)
            yield return Create(null, face.Paint, polygon.Select(p => Lift(triangle, p)).ToArray());

        MapTerrainPartition Create(string? paintId, MapPaintCoverage paint, IReadOnlyList<MapExactPoint> polygon)
        {
            admission.ReserveBytes(checked(128 + 48L * polygon.Count));
            return new(face.Face.Key, paintId, paint, polygon);
        }
    }

    static MapExactPoint Lift(MapExactTriangle triangle, MapWideXz p) =>
        new(Narrow(p.X), Narrow(MapEffectiveGeometry.HeightWide(triangle, p)), Narrow(p.Z));
    internal static MapExactValue Narrow(MapWideRational value) => value.TryNarrow(out var exact) ? exact : throw new MapExactOverflowException();

    internal static IReadOnlyDictionary<FeatherKey, IReadOnlyList<Edge>> Boundaries(
        IReadOnlyList<MapTerrainPartition> partitions, MapSceneAdmission admission)
    {
        var result = new Dictionary<FeatherKey, IReadOnlyList<Edge>>();
        foreach (var group in partitions.Where(p => p.Paint.Feather && p.Paint.OverlayCovers && p.Paint.Overlay != 0 &&
            p.Paint.Underlay != 0 && p.Paint.Underlay != p.Paint.Overlay).GroupBy(Key))
        {
            var lines = new Dictionary<Line, SortedDictionary<MapWideRational, (MapWideXz Point, int Delta)>>();
            foreach (var partition in group)
            {
                var polygon = MapWideRefinementGeometry.Normalize(partition.Polygon.Select(p => MapWidePoint.From(p).Xz));
                for (int i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                    if (a == b) continue;
                    admission.ReserveBytes(256);
                    bool vertical = a.X.Equals(b.X);
                    var slope = vertical ? default : b.Z.Subtract(a.Z).Divide(b.X.Subtract(a.X));
                    var line = new Line(vertical, slope, vertical ? a.X : a.Z.Subtract(slope.Multiply(a.X)));
                    if (!lines.TryGetValue(line, out var events)) lines.Add(line, events = new());
                    var first = vertical ? a.Z : a.X; var last = vertical ? b.Z : b.X;
                    // Keep the polygon's direction. Sorting the events then cancels opposite copies,
                    // including one long edge opposed by several collinear subedges.
                    Add(first, a, 1); Add(last, b, -1);
                    void Add(MapWideRational coordinate, MapWideXz point, int delta)
                    {
                        events.TryGetValue(coordinate, out var before);
                        events[coordinate] = (point, before.Delta + delta);
                    }
                }
            }
            var edges = new List<Edge>();
            foreach (var events in lines.Values)
            {
                int winding = 0;
                MapWideXz start = default;
                foreach (var (_, change) in events)
                {
                    int next = winding + change.Delta;
                    if (winding == 0 && next != 0) start = change.Point;
                    if (winding != 0 && next == 0) edges.Add(new(start, change.Point));
                    winding = next;
                }
                if (winding != 0) throw new MapDocumentException("Terrain feather boundary is open.");
            }
            result.Add(group.Key, edges.AsReadOnly());
        }
        return result;
    }
}
