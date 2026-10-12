using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Canonical geometry and conservative cell discovery in an effective world context.</summary>
internal static class MapEffectiveGeometry
{
    internal static MapFramePoint Presentation(MapScopedSurfaces view, MapExactPoint world) => new(view.Scope.Frame, new(
        world.X.Subtract(new((long)view.Scope.Frame.X * (long)KhaozEngine.Primitives.WorldFrame.Grid, 1)).ToSingle(), world.Y.ToSingle(),
        world.Z.Subtract(new((long)view.Scope.Frame.Z * (long)KhaozEngine.Primitives.WorldFrame.Grid, 1)).ToSingle()));
    internal static bool InScope(MapScopedSurfaces view, MapExactPoint world)
    {
        var rectangle = view.Scope.WorldRectangle();
        return world.X.CompareTo(rectangle.MinX) >= 0 && world.X.CompareTo(rectangle.MaxX) <= 0 &&
            world.Z.CompareTo(rectangle.MinZ) >= 0 && world.Z.CompareTo(rectangle.MaxZ) <= 0 &&
            (view.Scope.MinY is not { } low || world.Y.CompareTo(MapExactValue.FromSingle(low)) >= 0) &&
            (view.Scope.MaxY is not { } high || world.Y.CompareTo(MapExactValue.FromSingle(high)) <= 0);
    }
    internal static MapCompiledPatch Patch(MapScopedSurfaces view, MapPatchKey key)
    {
        if (!view.TryAcquiredPatch(key, out var patch, out var status) || patch is null)
            throw new MapDocumentException($"Missing effective patch '{key}' ({status}).");
        return TransformPatch(view, MapSurfaceCompiler.Compile(MapSpaceGeometry.Surface(view, key.SurfaceId), patch));
    }
    internal static MapCompiledStrip Strip(MapScopedSurfaces view, MapWallStrip strip) => MapWallStripCompiler.Compile(strip,
        MapBoundaryGeometry.ResolveChain(MapSpaceGeometry.Record<MapBoundaryChain>(view, strip.LowerChain), view),
        MapBoundaryGeometry.ResolveChain(MapSpaceGeometry.Record<MapBoundaryChain>(view, strip.UpperChain), view));
    internal static MapCompiledPatch TransformPatch(MapScopedSurfaces view, MapCompiledPatch local)
    {
        var kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, local.Key.SurfaceId);
        if (kernel is null || local.ExactVertices.Count == 0) return local;
        var vertices = local.ExactVertices.Select(kernel.Point).ToArray();
        var anchor = new MapSubmissionAnchor(vertices.Min(v => v.X.Floor()), vertices.Min(v => v.Y.Floor()), vertices.Min(v => v.Z.Floor()));
        var faces = local.Faces.Select(f => f with { Normal = Normal(new(vertices[f.A], vertices[f.B], vertices[f.C])) }).ToArray();
        return new(local.Key, local.Role, anchor, local.VertexIds, vertices, vertices.Select(v => MapSubmissionGeometry.Offset(v, anchor)),
            faces, local.Paint, local.LegacyFallbackCells);
    }
    internal static Vector3 Normal(MapExactTriangle t)
    {
        var ax = W(t.B.X).Subtract(W(t.A.X)); var ay = W(t.B.Y).Subtract(W(t.A.Y)); var az = W(t.B.Z).Subtract(W(t.A.Z));
        var bx = W(t.C.X).Subtract(W(t.A.X)); var by = W(t.C.Y).Subtract(W(t.A.Y)); var bz = W(t.C.Z).Subtract(W(t.A.Z));
        double x = ay.Multiply(bz).Subtract(az.Multiply(by)).ToDouble();
        double y = az.Multiply(bx).Subtract(ax.Multiply(bz)).ToDouble();
        double z = ax.Multiply(by).Subtract(ay.Multiply(bx)).ToDouble();
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length) || length == 0) throw new MapDocumentException("Collapsed effective triangle.", new MapExactOverflowException());
        return new((float)(x / length), (float)(y / length), (float)(z / length));
    }
    internal static MapExactPoint Point(MapScopedSurfaces view, string surfaceId, MapExactPoint local) =>
        view.EffectiveContext?.Point(MapPrefabChildKind.Surface, surfaceId, local) ?? local;
    internal static MapExactXz Vertex(MapScopedSurfaces view, MapLatticeVertex vertex)
    {
        var local = MapSpaceGeometry.Surface(view, vertex.SurfaceId).Frame.WorldXz(vertex.Address);
        return view.EffectiveContext?.Xz(vertex.SurfaceId, local) ?? local;
    }
    internal static IReadOnlyList<MapExactXz> CellPolygon(MapScopedSurfaces view, MapPatchKey key, int slotCell)
    {
        var surface = MapSpaceGeometry.Surface(view, key.SurfaceId);
        long x = checked(key.SlotX * 64 + slotCell % 64), z = checked(key.SlotZ * 64 + slotCell / 64);
        return new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + 1, z),
            MapLatticeAddress.Corner(x + 1, z + 1), MapLatticeAddress.Corner(x, z + 1) }.Select(a => Vertex(view, new(key.SurfaceId, a))).ToArray();
    }
    internal static MapExactXz Vertex(MapEffectiveContext context, MapLatticeVertex vertex) =>
        context.Xz(vertex.SurfaceId, context.Surface(vertex.SurfaceId).Frame.WorldXz(vertex.Address));
    internal static IReadOnlyList<MapExactXz> CellPolygon(MapEffectiveContext context, MapPatchKey key, int slotCell)
    {
        long x = checked(key.SlotX * 64 + slotCell % 64), z = checked(key.SlotZ * 64 + slotCell / 64);
        return new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + 1, z),
            MapLatticeAddress.Corner(x + 1, z + 1), MapLatticeAddress.Corner(x, z + 1) }.Select(a => Vertex(context, new(key.SurfaceId, a))).ToArray();
    }
    internal static MapCellRect CellRange(MapEffectiveContext context, string id, MapExactXz min, MapExactXz max) =>
        CellRange(context.Surface(id).Frame, context.Transform(MapPrefabChildKind.Surface, id), min, max);
    internal static IReadOnlyList<(long X, long Z)> CellCandidates(MapScopedSurfaces view, string surfaceId, MapExactXz world)
    {
        var surface = MapSpaceGeometry.Surface(view, surfaceId);
        var kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surfaceId);
        if (kernel is null) return new[] { MapSpaceGeometry.Cell(surface.Frame, world) };
        var (x, z) = InverseCell(surface.Frame, kernel.Transform, world.X.ToDouble(), world.Z.ToDouble());
        var result = new List<(long, long)>();
        for (long row = checked(z - 1); row <= checked(z + 1); row++)
            for (long column = checked(x - 1); column <= checked(x + 1); column++)
            {
                var key = MapPatchKey.ForCell(surfaceId, column, row);
                var polygon = CellPolygon(view, key, MapLowerCellClassifier.SlotCell(column, row));
                if (Contains(polygon, world, halfOpen: true)) result.Add((column, row));
            }
        return result.AsReadOnly();
    }
    internal static MapCellRect CellRange(MapScopedSurfaces view, string id, MapExactXz min, MapExactXz max)
    {
        var surface = MapSpaceGeometry.Surface(view, id);
        var kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, id);
        return CellRange(surface.Frame, kernel, min, max);
    }
    static MapCellRect CellRange(MapLatticeFrame frame, MapResourceTransform? kernel, MapExactXz min, MapExactXz max)
    {
        if (kernel is null) return MapLatticeRanges.CellRange(frame, min, max);
        var corners = new[] { min, max, new(min.X, max.Z), new(max.X, min.Z) }
            .Select(p => InverseCell(frame, kernel.Transform, p.X.ToDouble(), p.Z.ToDouble())).ToArray();
        return new(checked(corners.Min(c => c.X) - 1), checked(corners.Min(c => c.Z) - 1),
            checked(corners.Max(c => c.X) + 2), checked(corners.Max(c => c.Z) + 2));
    }
    static (long X, long Z) InverseCell(MapLatticeFrame frame, MapEffectiveTransform pose, double worldX, double worldZ)
    {
        var (s, c) = Math.SinCos(pose.YawRadians);
        double dx = worldX - pose.X, dz = worldZ - pose.Z;
        double x = (c * dx - s * dz) / pose.Scale / frame.CellUnitMetres.Exact().ToDouble();
        double z = (s * dx + c * dz) / pose.Scale / frame.CellUnitMetres.Exact().ToDouble();
        if (frame.RowDirection == MapRowDirection.NegativeZ) z = -z;
        return (checked((long)Math.Floor(x)), checked((long)Math.Floor(z)));
    }
    internal static bool Contains(IReadOnlyList<MapExactXz> polygon, MapExactXz point, bool halfOpen = false)
    {
        int sign = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            int cross = Cross(MapWideXz.From(polygon[i]), MapWideXz.From(polygon[(i + 1) % polygon.Count]), MapWideXz.From(point)).Sign;
            if (cross == 0) { if (halfOpen && i is 1 or 2) return false; continue; }
            if (sign != 0 && sign != cross) return false;
            sign = cross;
        }
        return sign != 0;
    }
    internal static MapExactRect Bounds(IEnumerable<MapExactXz> polygon)
    {
        var points = polygon.ToArray();
        return new(points.Min(p => p.X), points.Min(p => p.Z), points.Max(p => p.X), points.Max(p => p.Z));
    }

    internal static MapSpacePointBounds.Bound PlanPoint(MapScopedSurfaces view, MapBoundPreparation preparation,
        MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz point)
    {
        if (bound.Kind == MapBoundKind.OpenTop) return new(MapMembershipStatus.Resolved, null, null, null, null);
        var opening = bound.Kind == MapBoundKind.HorizontalOpening ? MapSpaceGeometry.Record<MapHorizontalOpening>(view, bound.Opening!) : null;
        string id = opening?.Patch.SurfaceId ?? bound.SurfaceId ?? throw new MapDocumentException("Missing effective bound surface.");
        foreach (var (x, z) in CellCandidates(view, id, point))
        {
            var key = MapPatchKey.ForCell(id, x, z);
            int slot = MapLowerCellClassifier.SlotCell(x, z);
            if (!view.TryAcquiredPatch(key, out var patch, out var status)) return new(MapMembershipStatus.MissingGeometry, null, null, null, $"patch {key}: {status}");
            if (patch is null) continue;
            int lx = slot % 64 - patch.CellMinX, lz = slot / 64 - patch.CellMinZ;
            if (lx < 0 || lx >= patch.Width || lz < 0 || lz >= patch.Depth) continue;
            if (opening is not null ? opening.Patch != key || !opening.SlotCells.Contains(slot) : !patch.IsPresent(lx, lz)) continue;
            if (preparation.ValidatedPatch(MapSpaceGeometry.Surface(view, id), patch) is null)
                return new(MapMembershipStatus.CapacityExceeded, null, null, null, preparation.Refusal);
            return new(MapMembershipStatus.Resolved, new(key, slot), bound.Opening, null, null);
        }
        return new(MapMembershipStatus.MissingGeometry, null, null, null, "Missing effective bound at exact XZ.");
    }

    internal static MapCellRefinement RefinePolygon(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper,
        IReadOnlyList<MapExactXz> boundary, MapRefinementLimits limits, MapRefinementWork? work = null)
    {
        try
        {
            if (lower.Count > limits.MaxSourceFacesPerBound || upper?.Count > limits.MaxSourceFacesPerBound)
                return MapCommonRefinement.Failure(MapRefinementStatus.CapacityExceeded, "source faces");
            var clip = boundary.Select(MapWideXz.From).ToList();
            if (AreaWide(clip).Sign == 0) return MapCommonRefinement.Failure(MapRefinementStatus.Invalid, "cell");
            if (AreaWide(clip).Sign < 0) clip.Reverse();
            var bottom = ClipFaces(lower); var top = upper is null ? new List<WideFace>() : ClipFaces(upper);
            long pairs = upper is null ? 0 : checked((long)bottom.Count * top.Count);
            if (work is not null) work.ChargedPairs = pairs;
            if (pairs > limits.MaxPairChecks) return MapCommonRefinement.Failure(MapRefinementStatus.CapacityExceeded, "pair checks");
            var vertices = new SortedSet<MapExactXz>();
            var faces = new List<MapRefinementFace>();
            MapWideRational? separation = null;
            foreach (var lo in bottom)
                if (upper is null) { if (!Emit(lo, null, lo.Polygon)) return Capacity(); }
                else foreach (var hi in top)
                {
                    if (work is not null) work.BoundingBoxComparisons++;
                    if (!WideBoundsOverlap(lo.Polygon, hi.Polygon)) continue;
                    if (work is not null) work.PositiveBoundingBoxPairs++;
                    var intersection = Intersect(lo.Polygon, hi.Polygon);
                    if (AreaWide(intersection).Sign == 0) continue;
                    if (work is not null) work.PositiveIntersections++;
                    if (!Emit(lo, hi, intersection)) return Capacity();
                }
            return new(MapRefinementStatus.Complete, Array.AsReadOnly(vertices.ToArray()), faces.AsReadOnly(),
                Narrow(AreaWide(clip)), Narrow(Sum(bottom)), default, Array.Empty<MapLegacyCellTag>(), upper is null ? null : Narrow(Sum(top)),
                separation is { } gap ? Narrow(gap) : null, (int)pairs, null);

            List<WideFace> ClipFaces(IReadOnlyList<MapBoundFace> source)
            {
                var result = new List<WideFace>();
                foreach (var face in source.OrderBy(f => f.Key))
                {
                    var t = face.Triangle;
                    if (MapSubdividedTriangle.CrossWide(t.A, t.B, t.C).Sign == 0) throw new MapDocumentException("degenerate triangle");
                    var triangle = new[] { new MapExactXz(t.A.X, t.A.Z), new(t.B.X, t.B.Z), new(t.C.X, t.C.Z) }.Select(MapWideXz.From).ToList();
                    if (AreaWide(triangle).Sign < 0) triangle.Reverse();
                    var polygon = Intersect(triangle, clip);
                    if (AreaWide(polygon).Sign > 0) result.Add(new(face, polygon));
                }
                return result;
            }
            bool Emit(WideFace lo, WideFace? hi, IReadOnlyList<MapWideXz> polygon)
            {
                if (faces.Count >= limits.MaxRefinementFaces) return false;
                var points = new List<MapExactXz>();
                MapWideRational? faceSeparation = null;
                foreach (var wide in polygon)
                {
                    if (!wide.TryNarrow(out var point)) throw new MapExactOverflowException();
                    if (!vertices.Contains(point) && vertices.Count >= limits.MaxRefinementVertices) return false;
                    vertices.Add(point); points.Add(point);
                    if (hi is not null)
                    {
                        var delta = HeightWide(hi.Face.Triangle, wide).Subtract(HeightWide(lo.Face.Triangle, wide));
                        faceSeparation = faceSeparation is { } current ? MapWideRational.Min(current, delta) : delta;
                        separation = separation is { } total ? MapWideRational.Min(total, delta) : delta;
                    }
                }
                faces.Add(new(lo.Face.Key, hi?.Face.Key, Normalize(points), faceSeparation is { } gap ? Narrow(gap) : null));
                return true;
            }
            MapCellRefinement Capacity() => MapCommonRefinement.Failure(MapRefinementStatus.CapacityExceeded, "refinement faces or vertices");
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return MapCommonRefinement.Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error) { return MapCommonRefinement.Failure(MapRefinementStatus.Invalid, error.Message); }
    }
    sealed record WideFace(MapBoundFace Face, IReadOnlyList<MapWideXz> Polygon);
    static MapWideRational Sum(IEnumerable<WideFace> faces) => faces.Aggregate(default(MapWideRational), (sum, f) => sum.Add(AreaWide(f.Polygon)));
    static bool WideBoundsOverlap(IReadOnlyList<MapWideXz> a, IReadOnlyList<MapWideXz> b) =>
        a.Min(p => p.X).CompareTo(b.Max(p => p.X)) < 0 && b.Min(p => p.X).CompareTo(a.Max(p => p.X)) < 0 &&
        a.Min(p => p.Z).CompareTo(b.Max(p => p.Z)) < 0 && b.Min(p => p.Z).CompareTo(a.Max(p => p.Z)) < 0;
    internal static MapWideRational AreaWide(IReadOnlyList<MapWideXz> polygon)
    {
        MapWideRational sum = default;
        if (polygon.Count < 3) return sum;
        // Offset cross products keep large absolute origins out of the arithmetic magnitudes.
        for (int i = 1; i + 1 < polygon.Count; i++) sum = sum.Add(Cross(polygon[0], polygon[i], polygon[i + 1]));
        return sum.Divide(MapWideRational.From(2));
    }
    static MapWideRational Cross(MapWideXz a, MapWideXz b, MapWideXz c) =>
        b.X.Subtract(a.X).Multiply(c.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(c.X.Subtract(a.X)));
    static MapWideRational HeightWide(MapExactTriangle t, MapWideXz p)
    {
        var a = new MapWideXz(W(t.A.X), W(t.A.Z)); var b = new MapWideXz(W(t.B.X), W(t.B.Z)); var c = new MapWideXz(W(t.C.X), W(t.C.Z));
        var area = Cross(a, b, c);
        return W(t.A.Y).Add(W(t.B.Y).Subtract(W(t.A.Y)).Multiply(Cross(a, p, c).Divide(area)))
            .Add(W(t.C.Y).Subtract(W(t.A.Y)).Multiply(Cross(a, b, p).Divide(area)));
    }
    static List<MapWideXz> Intersect(IReadOnlyList<MapWideXz> source, IReadOnlyList<MapWideXz> clip)
    {
        var output = source.ToList();
        for (int i = 0; i < clip.Count && output.Count != 0; i++)
        {
            var input = output; output = new();
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];
            var previous = input[^1]; var previousSide = Cross(a, b, previous);
            foreach (var current in input)
            {
                var side = Cross(a, b, current);
                if ((side.Sign >= 0) != (previousSide.Sign >= 0))
                {
                    var fraction = previousSide.Divide(previousSide.Subtract(side));
                    output.Add(new(previous.X.Add(current.X.Subtract(previous.X).Multiply(fraction)), previous.Z.Add(current.Z.Subtract(previous.Z).Multiply(fraction))));
                }
                if (side.Sign >= 0) output.Add(current);
                previous = current; previousSide = side;
            }
        }
        return output.Distinct().ToList();
    }
    static IReadOnlyList<MapExactXz> Normalize(List<MapExactXz> polygon)
    {
        if (AreaWide(polygon.Select(MapWideXz.From).ToArray()).Sign < 0) polygon.Reverse();
        int first = 0;
        for (int i = 1; i < polygon.Count; i++) if (polygon[i].CompareTo(polygon[first]) < 0) first = i;
        return Array.AsReadOnly(Enumerable.Range(0, polygon.Count).Select(i => polygon[(first + i) % polygon.Count]).ToArray());
    }
    internal static MapExactValue Area(IReadOnlyList<MapExactXz> polygon) => Narrow(AreaWide(polygon.Select(MapWideXz.From).ToArray()).Abs());
    static MapWideRational W(MapExactValue value) => MapWideRational.From(value);
    static MapExactValue Narrow(MapWideRational value) => MapSubdividedTriangle.Narrow(value);
}
