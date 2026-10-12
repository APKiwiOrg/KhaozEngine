using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Clips and partitions paint in wide precision, then narrows only the published material polygons.</summary>
public static class MapPartialPaintCompiler
{
    public static MapPaintRegionSet Compile(MapScopedSurfaces view, string targetSurfaceId, MapRefinementLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSurfaceId);
        var budget = limits ?? new();
        if (view.Status != MapAcquireStatus.Complete)
            throw new MapDocumentException((view.Status == MapAcquireStatus.Incomplete ? "MissingGeometry" : view.Status.ToString()) + ": " + view.Detail);
        if (!view.Scope.Roles.Contains(MapSurfaceRole.PaintOverride))
            throw new MapDocumentException("MissingGeometry: paint compilation requires complete acquired paint and target geometry.");
        var target = view.Surfaces.SingleOrDefault(s => s.Id == targetSurfaceId);
        if (target is null || target.Role == MapSurfaceRole.PaintOverride)
            throw new MapDocumentException("Invalid: paint target must be a declared physical surface.");
        var paints = view.Surfaces.Where(s => s.Role == MapSurfaceRole.PaintOverride && s.PaintTargetSurfaceId == targetSurfaceId &&
            (view.EffectiveContext is null || view.EffectiveContext.IsPartialPaint(s)))
            .OrderByDescending(s => s.PaintPriority).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        foreach (var surface in view.Surfaces) MapSurfaceJson.RequirePaintPriority(surface);
        if (paints.Length == 0) return new([], Digest([]));
        try { return CompileCore(view, target, paints, budget); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapDocumentException("NotRepresentable: paint intersection cannot be emitted exactly. No regions were published.", error); }
    }

    static MapPaintRegionSet CompileCore(MapScopedSurfaces view, MapSurfaceRef target, MapSurfaceRef[] paints, MapRefinementLimits limits)
    {
        var work = new Work(limits);
        var refs = paints.Append(target).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var keys = view.Witness.Present.Select(p => p.Key).Where(k => refs.ContainsKey(k.SurfaceId)).Order().ToArray();
        work.Require(keys.Length <= limits.MaxContextPatches, "context patches");
        var inputs = keys.Select(key =>
        {
            if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null)
                throw new MapDocumentException("MissingGeometry: paint or target patch unavailable.");
            var surface = refs[key.SurfaceId];
            var adapter = surface.Role == MapSurfaceRole.PaintOverride ? surface with { Role = MapSurfaceRole.SupportFloor, PaintPriority = 0 } : surface;
            return (Surface: surface, Adapter: adapter, Patch: patch,
                Faces: MapSurfaceCompiler.CountFaces(adapter, patch, MapSlotCellMask.All));
        }).ToArray();
        long targetCount = inputs.Where(p => p.Surface.Id == target.Id).Sum(p => p.Faces);
        long paintCount = inputs.Where(p => p.Surface.Id != target.Id).Sum(p => p.Faces);
        work.Require(targetCount <= limits.MaxSourceFacesPerBound && paintCount <= limits.MaxSourceFacesPerBound, "source faces");
        work.Require(targetCount + paintCount <= limits.MaxContextFaces, "context faces");
        var patches = inputs.Select(p => (p.Surface, Geometry: MapSurfaceCompiler.Compile(p.Adapter, p.Patch))).ToArray();
        var source = patches.Where(p => p.Surface.Id != target.Id).GroupBy(p => p.Surface)
            .SelectMany(group => PaintSources(view, group.Key, group.Select(p => p.Geometry), work))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Material.Face).ToArray();
        var regions = new List<MapPaintRegion>();
        foreach (var patch in patches.Where(p => p.Surface.Id == target.Id))
        {
            var geometry = MapEffectiveGeometry.TransformPatch(view, patch.Geometry);
            foreach (var face in geometry.Faces)
            {
                var triangle = geometry.ExactTriangle(face);
                var parent = Polygon(triangle);
                var candidates = new List<Source>();
                foreach (var paint in source)
                {
                    work.Pair();
                    if (!BoundsOverlap(parent, paint.Polygon)) continue;
                    var clipped = MapWideRefinementGeometry.Normalize(MapEffectiveGeometry.Intersect(paint.Polygon, parent));
                    if (MapEffectiveGeometry.AreaWide(clipped).Sign == 0) continue;
                    candidates.Add(paint with { Material = paint.Material with { Face = face.Key }, Polygon = clipped });
                    work.Polygons(candidates.Select(c => c.Polygon));
                }
                // Check authored conflicts before occlusion, including conflicts hidden by higher-priority paint.
                for (int i = 0; i < candidates.Count; i++)
                    for (int j = i + 1; j < candidates.Count && candidates[j].Priority == candidates[i].Priority; j++)
                    {
                        var a = candidates[i]; var b = candidates[j];
                        if (a.Material == b.Material) continue;
                        work.Pair();
                        if (BoundsOverlap(a.Polygon, b.Polygon) && MapEffectiveGeometry.AreaWide(MapEffectiveGeometry.Intersect(a.Polygon, b.Polygon)).Sign > 0)
                            throw new MapDocumentException($"Invalid: equal-priority paint '{a.Id}' and '{b.Id}' overlap with conflicting materials. Author an explicit priority.");
                    }
                var accepted = new List<Source>();
                foreach (var candidate in candidates)
                {
                    var pieces = new List<IReadOnlyList<MapWideXz>> { candidate.Polygon };
                    foreach (var owner in accepted)
                    {
                        var remaining = new List<IReadOnlyList<MapWideXz>>();
                        foreach (var piece in pieces)
                        {
                            work.Pair();
                            if (!BoundsOverlap(piece, owner.Polygon)) remaining.Add(piece);
                            else remaining.AddRange(Subtract(piece, owner.Polygon));
                            work.Polygons(remaining);
                        }
                        pieces = remaining;
                        if (pieces.Count == 0) break;
                    }
                    foreach (var piece in pieces)
                    {
                        accepted.Add(candidate with { Polygon = piece });
                        work.Polygons(accepted.Select(c => c.Polygon));
                    }
                }
                foreach (var region in accepted)
                {
                    var points = region.Polygon.Select(p => Lift(triangle, p)).ToArray();
                    work.Emit(points.Length);
                    regions.Add(new(face.Key, region.Id, region.Priority, region.Material, points));
                }
            }
        }
        return new(regions, Digest(regions));
    }

    static IEnumerable<Source> PaintSources(MapScopedSurfaces view, MapSurfaceRef surface,
        IEnumerable<MapCompiledPatch> patches, Work work)
    {
        // Cancel internal material edges in local space before the one point freeze. Paint height is irrelevant.
        var materials = patches.SelectMany(p => p.Faces.Select((face, i) =>
            (Material: p.Paint[i] with { Face = default }, Polygon: Polygon(p.ExactTriangle(face)))))
            .GroupBy(p => p.Material).ToArray();
        var boundaries = materials.Select(group =>
        {
            var edges = new HashSet<Edge>();
            foreach (var face in group)
                for (int i = 0; i < face.Polygon.Count; i++)
                {
                    var edge = new Edge(face.Polygon[i], face.Polygon[(i + 1) % face.Polygon.Count]);
                    if (!edges.Remove(new(edge.B, edge.A)) && !edges.Add(edge))
                        throw new MapDocumentException("Invalid: non-manifold paint material boundary.");
                }
            return (group.Key, Edges: edges);
        }).ToArray();
        var junctions = boundaries.SelectMany(b => b.Edges.SelectMany(e => new[] { e.A, e.B }).Distinct())
            .GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var boundary in boundaries)
        {
            var edges = boundary.Edges;
            var incoming = edges.GroupBy(e => e.B).ToDictionary(g => g.Key, g => g.ToArray());
            var outgoing = edges.GroupBy(e => e.A).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var vertex in outgoing.Keys.Order().ToArray())
            {
                if (junctions.Contains(vertex) || outgoing[vertex].Length != 1 || !incoming.TryGetValue(vertex, out var before) || before.Length != 1) continue;
                var first = before[0]; var second = outgoing[vertex][0];
                var cross = first.B.X.Subtract(first.A.X).Multiply(second.B.Z.Subtract(second.A.Z))
                    .Subtract(first.B.Z.Subtract(first.A.Z).Multiply(second.B.X.Subtract(second.A.X)));
                if (cross.Sign != 0) continue;
                var combined = new Edge(first.A, second.B);
                edges.Remove(first); edges.Remove(second); edges.Add(combined);
                outgoing[first.A] = outgoing[first.A].Select(e => e == first ? combined : e).ToArray();
                incoming[second.B] = incoming[second.B].Select(e => e == second ? combined : e).ToArray();
            }
            var points = new Dictionary<MapWideXz, MapWideXz>();
            MapWideXz Freeze(MapWideXz local)
            {
                if (points.TryGetValue(local, out var frozenPoint)) return frozenPoint;
                // These are existing authored endpoints, never generated region intersections.
                var point = MapWideRefinementGeometry.Narrow(local);
                frozenPoint = MapWideXz.From(view.EffectiveContext?.Xz(surface.Id, point) ?? point);
                points.Add(local, frozenPoint);
                return frozenPoint;
            }
            var frozen = edges.OrderBy(e => e.A).ThenBy(e => e.B).Select(e => new Edge(Freeze(e.A), Freeze(e.B))).ToArray();
            // Even-odd slabs handle concave outlines and holes without inventing an independent paint floor.
            var levels = frozen.SelectMany(e => new[] { e.A.Z, e.B.Z }).Distinct().Order().ToArray();
            var polygons = new List<IReadOnlyList<MapWideXz>>();
            for (int i = 1; i < levels.Length; i++)
            {
                var low = levels[i - 1]; var high = levels[i];
                var middle = low.Add(high).Divide(MapWideRational.From(2));
                var crossings = new List<(MapWideRational X, Edge Edge)>();
                foreach (var edge in frozen)
                {
                    work.Pair();
                    if (MapWideRational.Min(edge.A.Z, edge.B.Z).CompareTo(middle) < 0 &&
                        MapWideRational.Max(edge.A.Z, edge.B.Z).CompareTo(middle) > 0)
                        crossings.Add((AtZ(edge, middle), edge));
                }
                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                if (crossings.Count % 2 != 0) throw new MapDocumentException("Invalid: open paint material boundary.");
                for (int j = 0; j < crossings.Count; j += 2)
                {
                    var left = crossings[j].Edge; var right = crossings[j + 1].Edge;
                    var polygon = MapWideRefinementGeometry.Normalize(new[] { new MapWideXz(AtZ(left, low), low),
                        new(AtZ(right, low), low), new(AtZ(right, high), high), new(AtZ(left, high), high) });
                    if (MapEffectiveGeometry.AreaWide(polygon).Sign == 0) continue;
                    polygons.Add(polygon);
                    work.Polygons(polygons);
                }
            }
            foreach (var polygon in polygons)
            {
                work.Source(polygon.Count);
                yield return new(surface.Id, surface.PaintPriority, boundary.Key, polygon);
            }
        }
    }

    static MapWideRational AtZ(Edge edge, MapWideRational z) => edge.A.X.Add(edge.B.X.Subtract(edge.A.X)
        .Multiply(z.Subtract(edge.A.Z).Divide(edge.B.Z.Subtract(edge.A.Z))));

    static IReadOnlyList<MapWideXz> Polygon(MapExactTriangle triangle) => MapWideRefinementGeometry.Normalize(new[]
    { MapWidePoint.From(triangle.A).Xz, MapWidePoint.From(triangle.B).Xz, MapWidePoint.From(triangle.C).Xz });

    static bool BoundsOverlap(IReadOnlyList<MapWideXz> a, IReadOnlyList<MapWideXz> b) =>
        a.Min(p => p.X).CompareTo(b.Max(p => p.X)) < 0 && b.Min(p => p.X).CompareTo(a.Max(p => p.X)) < 0 &&
        a.Min(p => p.Z).CompareTo(b.Max(p => p.Z)) < 0 && b.Min(p => p.Z).CompareTo(a.Max(p => p.Z)) < 0;

    static IEnumerable<IReadOnlyList<MapWideXz>> Subtract(IReadOnlyList<MapWideXz> polygon, IReadOnlyList<MapWideXz> clip)
    {
        var inside = polygon;
        for (int i = 0; i < clip.Count && inside.Count >= 3; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];
            var outside = MapWideRefinementGeometry.Normalize(MapEffectiveGeometry.ClipHalfPlane(inside, a, b, inside: false));
            if (MapEffectiveGeometry.AreaWide(outside).Sign > 0) yield return outside;
            inside = MapEffectiveGeometry.ClipHalfPlane(inside, a, b);
        }
    }

    // The only bounded adapter for generated paint geometry. No intermediate polygon is narrowed or rounded.
    static MapExactPoint Lift(MapExactTriangle triangle, MapWideXz point)
    {
        var wide = new MapWidePoint(point.X, MapEffectiveGeometry.HeightWide(triangle, point), point.Z);
        if (!wide.X.TryNarrow(out var x) || !wide.Y.TryNarrow(out var y) || !wide.Z.TryNarrow(out var z))
            throw new MapExactOverflowException();
        return new(x, y, z);
    }

    static string Digest(IReadOnlyList<MapPaintRegion> regions) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/paint-regions/1");
        foreach (var region in regions)
        {
            w.WriteStartArray(); w.WriteStringValue(region.Face.OwnerId);
            w.WriteStringValue(region.Face.Patch?.SurfaceId);
            w.WriteStringValue(region.Face.Patch?.SlotX.ToString(CultureInfo.InvariantCulture));
            w.WriteStringValue(region.Face.Patch?.SlotZ.ToString(CultureInfo.InvariantCulture));
            w.WriteNumberValue(region.Face.Primitive); w.WriteNumberValue(region.Face.ParentTriangle);
            w.WriteNumberValue(region.Face.Child); w.WriteNumberValue((byte)region.Face.Side);
            w.WriteStringValue(region.PaintId); w.WriteNumberValue(region.Priority);
            w.WriteNumberValue(region.Material.Underlay); w.WriteNumberValue(region.Material.Overlay);
            w.WriteBooleanValue(region.Material.OverlayCovers); w.WriteBooleanValue(region.Material.Feather);
            foreach (var p in region.Polygon)
            {
                w.WriteStartArray();
                foreach (var value in new[] { p.X, p.Y, p.Z })
                { w.WriteStringValue(value.Numerator.ToString(CultureInfo.InvariantCulture)); w.WriteStringValue(value.Denominator.ToString(CultureInfo.InvariantCulture)); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
    });

    sealed record Source(string Id, int Priority, MapPaintCoverage Material, IReadOnlyList<MapWideXz> Polygon);
    readonly record struct Edge(MapWideXz A, MapWideXz B);

    sealed class Work(MapRefinementLimits limits)
    {
        long _pairs, _regions, _vertices, _sources, _sourceVertices;
        internal void Require(bool condition, string detail)
        { if (!condition) throw new MapDocumentException("CapacityExceeded: paint " + detail + " budget exceeded."); }
        internal void Pair() => Require(++_pairs <= limits.MaxPairChecks, "pair checks");
        internal void Source(int vertices)
        {
            _sourceVertices += vertices;
            Require(++_sources <= limits.MaxRefinementFaces && _sourceVertices <= limits.MaxRefinementVertices, "source regions");
        }
        internal void Polygons(IEnumerable<IReadOnlyList<MapWideXz>> polygons)
        {
            long count = 0, vertices = 0;
            foreach (var p in polygons) { count++; vertices += p.Count; }
            Require(count <= limits.MaxRefinementFaces && vertices <= limits.MaxRefinementVertices, "intermediate regions");
        }
        internal void Emit(int vertices)
        {
            _vertices += vertices;
            Require(++_regions <= limits.MaxRefinementFaces && _vertices <= limits.MaxRefinementVertices, "output regions");
        }
    }
}
