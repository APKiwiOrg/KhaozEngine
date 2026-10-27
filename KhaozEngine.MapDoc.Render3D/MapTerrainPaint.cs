using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Render-only complements of the released disjoint paint regions, plus their true feather boundaries.
/// Shared canonical diagonals and collinear subdivisions cancel before any distance is sampled.</summary>
internal static class MapTerrainPaint
{
    internal readonly record struct FeatherKey(MapTerrainSheets.Node Sheet, ushort Material);
    internal readonly record struct Edge(MapWideXz A, MapWideXz B);
    internal static FeatherKey Key(MapTerrainPartition p) => new(MapTerrainSheets.Key(p),
        p.Paint.OverlayCovers && p.Paint.Overlay != 0 ? p.Paint.Overlay : p.Paint.Underlay);

    internal static IEnumerable<MapTerrainPartition> Partition(MapTerrainCanonicalFace face,
        IEnumerable<MapPaintRegion> published, MapSceneAdmission admission, MapPreparationReservation scratch)
    {
        int count = published.Count();
        long rows = checked(16L * count);
        scratch.Reserve(rows);
        try
        {
            var regions = published.ToArray();
            long edges = checked(3L + regions.Sum(r => (long)r.Polygon.Count));
            long bytes = face.Face.Role == MapFaceRole.Wall ? 144L : count == 0 ? 2048L
                : checked(2L * MapPaintPreparation.WorkspaceForFace(count, edges));
            scratch.Reserve(bytes);
            try
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
                foreach (var region in regions)
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
            finally { scratch.Release(bytes); }
        }
        finally { scratch.Release(rows); }
    }

    static MapExactPoint Lift(MapExactTriangle triangle, MapWideXz p) =>
        new(Narrow(p.X), Narrow(MapEffectiveGeometry.HeightWide(triangle, p)), Narrow(p.Z));
    internal static MapExactValue Narrow(MapWideRational value) => value.TryNarrow(out var exact) ? exact : throw new MapExactOverflowException();

    internal static IReadOnlyDictionary<FeatherKey, IReadOnlyList<Edge>> Boundaries(
        IReadOnlyList<MapTerrainPartition> partitions, MapScopedSurfaces view, MapSceneAdmission admission)
    {
        using var preparation = admission.BeginPreparation();
        var result = new Dictionary<FeatherKey, IReadOnlyList<Edge>>();
        if (!partitions.Any(NeedsFeather))
        {
            preparation.Complete(0);
            return result;
        }
        admission.ReserveBytes(checked(128L * partitions.Count));
        using var scratch = admission.Workspace();
        using var indexStorage = admission.Workspace();
        long indexBytes = 0, edgeBytes = 0;
        using var sheetStorage = admission.Workspace();
        var sheets = MapTerrainSheets.Connect(partitions, view, admission, sheetStorage);
        // A cut with another overlay cannot qualify through its uncovered underlay. Bare underlay
        // suppresses candidate overlay edges, but never supplies an outline of its own.
        foreach (var group in partitions.Where(p => p.Paint.Overlay == 0 || p.Paint.OverlayCovers)
            .GroupBy(p => new FeatherKey(sheets[MapTerrainSheets.Key(p)], Key(p).Material)).Where(g => g.Any(NeedsFeather)))
        {
            using var groupPreparation = admission.BeginPreparation();
            var lines = new Dictionary<MapTerrainLine, SortedDictionary<MapWideRational, (MapWideXz Point, int Overlay, int Coverage)>>();
            foreach (var partition in group)
            {
                long bytes = checked(256L + 256L * partition.Polygon.Count);
                scratch.Reserve(bytes);
                try
                {
                    var polygon = partition.Polygon.Select(MapWidePoint.From).ToArray();
                    if (MapEffectiveGeometry.AreaWide(polygon.Select(p => p.Xz).ToArray()).Sign < 0) Array.Reverse(polygon);
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                        if (a.Xz == b.Xz) continue;
                        admission.ReserveBytes(1024);
                        var line = MapTerrainLine.Through(a, b);
                        var first = line.Coordinate(a); var last = line.Coordinate(b);
                        if (!lines.TryGetValue(line, out var events)) lines.Add(line, events = new());
                        // Keep the polygon's direction. Sorting the events then cancels opposite copies,
                        // including one long edge opposed by several collinear subedges.
                        Add(first, a.Xz, 1); Add(last, b.Xz, -1);
                        void Add(MapWideRational coordinate, MapWideXz point, int delta)
                        {
                            events.TryGetValue(coordinate, out var before);
                            events[coordinate] = (point, before.Overlay + (partition.Paint.OverlayCovers ? delta : 0),
                                before.Coverage + delta);
                        }
                    }
                }
                finally { scratch.Release(bytes); }
            }
            var edges = new List<Edge>();
            foreach (var events in lines.Values)
            {
                int overlay = 0, coverage = 0;
                MapWideXz start = default;
                foreach (var (_, change) in events)
                {
                    bool before = overlay != 0 && coverage != 0;
                    overlay += change.Overlay; coverage += change.Coverage;
                    bool after = overlay != 0 && coverage != 0;
                    if (!before && after) start = change.Point;
                    if (before && !after) edges.Add(new(start, change.Point));
                }
                if (overlay != 0 || coverage != 0) throw new MapDocumentException("Terrain feather boundary is open.");
            }
            var rows = edges.AsReadOnly();
            long retained = checked(16L + 128L * edges.Capacity);
            edgeBytes = checked(edgeBytes + retained);
            groupPreparation.Complete(retained); groupPreparation.Dispose();
            foreach (var key in group.Where(NeedsFeather).Select(Key).Distinct())
            {
                MapPreparationCollections.Dictionary(result, indexStorage, ref indexBytes);
                result.Add(key, rows);
            }
        }
        preparation.AdoptWorkspace(indexStorage, indexBytes);
        preparation.Complete(checked(indexBytes + edgeBytes));
        return result;
    }

    static bool NeedsFeather(MapTerrainPartition p) => p.Paint.Feather && p.Paint.OverlayCovers &&
        p.Paint.Overlay != 0 && p.Paint.Underlay != 0 && p.Paint.Underlay != p.Paint.Overlay;
}
