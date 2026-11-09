using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

public sealed record MapWaterCellKey(string BodyId, string SpaceId, string FragmentId, MapTileCoord StorageOwner,
    long X, long Z, string GeometryPolicy);

/// <summary>A complete storage-local cell. Coordinates and geometry never depend on the requesting window.</summary>
public sealed record MapWaterResourceCell(MapWaterCellKey Key, float SurfaceY, MapWaterMediumDoc Medium,
    string RenderMaterial, IReadOnlyList<MapWaterRegion> Regions, string PayloadDigest)
{
    internal MapBox3 CellBounds { get; init; }
    public IReadOnlyList<MapWaterRegion> Regions { get; init; } = Array.AsReadOnly(Regions.Select(r => r with
    { Triangles = Array.AsReadOnly(r.Triangles.ToArray()) }).ToArray());
}

public sealed record MapScopedWaterResult(MapSceneStatus Status, IReadOnlyList<MapWaterResourceCell> Cells,
    MapSceneWitness RequestedWitness, MapSceneWitness AcquiredCellWitness) : IDisposable
{
    internal MapPreparationInventory? PreparationOwnership { get; init; }
    internal static long CellInventoryBytes(MapWaterResourceCell cell) =>
        checked(256L + cell.Regions.Sum(r => 128L + 144L * r.Triangles.Count));
    public void Dispose() => PreparationOwnership?.Dispose();
    public MapSceneCapacity? Capacity { get; init; }
    internal IReadOnlyList<MapWaterResourceCell> AcquiredCells { get; init; } = Array.Empty<MapWaterResourceCell>();
    internal double MotionPaddingMetres { get; init; }
    internal MapSceneLimits Limits { get; init; } = new();
    public long PreparationChargeBytes { get; init; }
    public long CandidateCells { get; init; }
    public long PairChecks { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

/// <summary>Canonical exact union of coplanar triangles. Vertical slabs meet only at boundary changes. Adjacent
/// slabs with the same lower and upper supporting lines coalesce, removing authored piece and face diagonals.</summary>
internal static class MapWaterCellGeometry
{
    readonly record struct Line(MapWideXz A, MapWideXz B)
    {
        internal MapWideRational At(MapWideRational x) => A.Z.Add(x.Subtract(A.X).Multiply(B.Z.Subtract(A.Z)).Divide(B.X.Subtract(A.X)));
        internal bool Same(Line other) => MapWaterPolygons.Cross(A, B, other.A).Sign == 0 && MapWaterPolygons.Cross(A, B, other.B).Sign == 0;
    }
    sealed record Band(MapWideRational MinX, MapWideRational MaxX, Line Low, Line High);
    sealed record Interval(Line Low, Line High, MapWideRational Min, MapWideRational Max);

    internal static IReadOnlyList<MapExactTriangle> Union(IReadOnlyList<MapExactTriangle> triangles, float y,
        MapWaterWorkBudget pairs, MapSceneAdmission admission, int maxVertices)
    {
        if (triangles.Count == 0) return Array.Empty<MapExactTriangle>();
        using var preparation = admission.BeginPreparation();
        admission.ReserveBytes(checked(512L + 384L * triangles.Count));
        var polygons = triangles.Select(t => new[] { MapWidePoint.From(t.A).Xz, MapWidePoint.From(t.B).Xz, MapWidePoint.From(t.C).Xz }).ToArray();
        var edges = polygons.SelectMany(p => Enumerable.Range(0, 3).Select(i => new Line(p[i], p[(i + 1) % 3])))
            .Where(e => !e.A.X.Equals(e.B.X)).ToArray();
        var xs = new SortedSet<MapWideRational>(polygons.SelectMany(p => p).Select(p => p.X));
        pairs.Charge(checked((long)edges.Length * (edges.Length - 1) / 2));
        for (int i = 0; i < edges.Length; i++)
            for (int j = 0; j < i; j++)
            {
                var a = edges[i]; var b = edges[j];
                var dx = a.B.X.Subtract(a.A.X); var dz = a.B.Z.Subtract(a.A.Z);
                var ex = b.B.X.Subtract(b.A.X); var ez = b.B.Z.Subtract(b.A.Z);
                var cross = dx.Multiply(ez).Subtract(dz.Multiply(ex));
                if (cross.Sign == 0) continue;
                var ox = b.A.X.Subtract(a.A.X); var oz = b.A.Z.Subtract(a.A.Z);
                var t = ox.Multiply(ez).Subtract(oz.Multiply(ex)).Divide(cross);
                var u = ox.Multiply(dz).Subtract(oz.Multiply(dx)).Divide(cross);
                if (t.Sign > 0 && t.CompareTo(MapWideRational.From(1)) < 0 && u.Sign > 0 && u.CompareTo(MapWideRational.From(1)) < 0)
                { admission.ReserveBytes(64); xs.Add(a.A.X.Add(t.Multiply(dx))); }
            }
        var cuts = xs.ToArray();
        var bands = new List<Band>();
        for (int slab = 0; slab + 1 < cuts.Length; slab++)
        {
            var left = cuts[slab]; var right = cuts[slab + 1];
            var middle = left.Add(right).Divide(MapWideRational.From(2));
            pairs.Charge(polygons.Length);
            using var intervalWorkspace = admission.Workspace();
            intervalWorkspace.Reserve(checked(polygons.Length * 128L));
            var intervals = new List<Interval>();
            foreach (var polygon in polygons)
            {
                var lines = new List<Line>(2);
                for (int i = 0; i < 3; i++)
                {
                    var line = new Line(polygon[i], polygon[(i + 1) % 3]);
                    if (MapWideRational.Min(line.A.X, line.B.X).CompareTo(middle) < 0 &&
                        middle.CompareTo(MapWideRational.Max(line.A.X, line.B.X)) < 0) lines.Add(line);
                }
                if (lines.Count != 2) continue;
                if (lines[0].At(middle).CompareTo(lines[1].At(middle)) > 0) (lines[0], lines[1]) = (lines[1], lines[0]);
                intervals.Add(new(lines[0], lines[1], lines[0].At(middle), lines[1].At(middle)));
            }
            intervals.Sort((a, b) => a.Min.CompareTo(b.Min));
            var merged = new List<Interval>();
            foreach (var interval in intervals)
            {
                if (merged.Count == 0 || merged[^1].Max.CompareTo(interval.Min) < 0) merged.Add(interval);
                else if (merged[^1].Max.CompareTo(interval.Max) < 0) merged[^1] = merged[^1] with { High = interval.High, Max = interval.Max };
            }
            foreach (var interval in merged)
            {
                int prior = bands.FindIndex(b => b.MaxX.Equals(left) && b.Low.Same(interval.Low) && b.High.Same(interval.High));
                if (prior >= 0) bands[prior] = bands[prior] with { MaxX = right };
                else { admission.ReserveBytes(192); bands.Add(new(left, right, interval.Low, interval.High)); }
            }
        }
        if (DemandDiagnostics.Check(admission, "MapWaterResourceCell.MaxEmittedVertices", "MaxEmittedVertices", 0, 6m * bands.Count, maxVertices, 6L * bands.Count > maxVertices, CapacityDemandLifetime.Inventory)) throw new MapWaterCapacityException("water emitted vertices");
        admission.ReserveCompilation(checked(2L * bands.Count));
        var result = new List<MapExactTriangle>();
        var level = MapExactValue.FromSingle(y);
        foreach (var band in bands.OrderBy(b => b.MinX).ThenBy(b => b.Low.At(b.MinX)).ThenBy(b => b.MaxX))
        {
            var points = new[] { new MapWideXz(band.MinX, band.Low.At(band.MinX)), new(band.MaxX, band.Low.At(band.MaxX)),
                new(band.MaxX, band.High.At(band.MaxX)), new(band.MinX, band.High.At(band.MinX)) }.Distinct().ToArray();
            if (points.Length < 3) continue;
            var first = Lift(points[0]);
            for (int i = 1; i + 1 < points.Length; i++)
            {
                var triangle = new MapExactTriangle(first, Lift(points[i + 1]), Lift(points[i]));
                if (MapWaterPolygons.Cross(points[0], points[i + 1], points[i]).Sign != 0) result.Add(triangle);
            }
        }
        preparation.Complete(checked(144L * result.Count));
        return Array.AsReadOnly(result.ToArray());
        MapExactPoint Lift(MapWideXz p)
        {
            var exact = MapWaterRegionGeometry.Drawing(p);
            return new(exact.X, level, exact.Z);
        }
    }
}
