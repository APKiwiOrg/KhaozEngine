using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact common refinement of independently authored rational bound lattices.</summary>
public static class MapCommonRefinement
{
    public static MapCellRefinement Refine(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits) => RefineCore(lower, upper, min, max, limits, null);

    internal static MapCellRefinement Refine(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits, MapRefinementWork work) =>
        RefineCore(lower, upper, min, max, limits, work);

    static MapCellRefinement RefineCore(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits, MapRefinementWork? work)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            if (min.X.CompareTo(max.X) >= 0 || min.Z.CompareTo(max.Z) >= 0)
                return Failure(MapRefinementStatus.Invalid, "cell");
            foreach (MapBoundFace face in lower.Concat(upper ?? Array.Empty<MapBoundFace>()))
                if (MapSubdividedTriangle.Cross(face.Triangle.A, face.Triangle.B, face.Triangle.C).Sign == 0)
                    return Failure(MapRefinementStatus.Invalid, "degenerate");
            if (lower.Count > limits.MaxSourceFacesPerBound || upper?.Count > limits.MaxSourceFacesPerBound)
                return Failure(MapRefinementStatus.CapacityExceeded, "source faces");
            List<Clipped> bottom = Clip(lower), top = upper is null ? new() : Clip(upper);
            MapExactValue cellArea = max.X.Subtract(min.X).Multiply(max.Z.Subtract(min.Z));
            MapExactValue lowerArea = Sum(bottom), upperArea = Sum(top);
            long pairs = upper is null ? 0 : checked((long)bottom.Count * top.Count);
            if (work is not null) work.ChargedPairs = pairs;
            if (pairs > limits.MaxPairChecks) return Failure(MapRefinementStatus.CapacityExceeded, "pair checks");
            var faces = new List<MapRefinementFace>();
            var vertices = new SortedSet<MapExactXz>();
            MapExactValue? separation = null;
            foreach (Clipped lo in bottom)
            {
                if (upper is null)
                {
                    if (!Emit(lo, null, lo.Polygon.ToList())) return Failure(MapRefinementStatus.CapacityExceeded, "refinement faces or vertices");
                    continue;
                }
                foreach (Clipped hi in top)
                {
                    if (work is not null) work.BoundingBoxComparisons++;
                    if (!lo.Bounds.Overlaps(hi.Bounds)) continue;
                    if (work is not null) work.PositiveBoundingBoxPairs++;
                    List<MapExactXz> polygon = MapRefinementPolygon.Intersect(lo.Polygon, hi.Polygon);
                    if (MapRefinementPolygon.Area(polygon).Sign == 0) continue;
                    if (work is not null) work.PositiveIntersections++;
                    if (!Emit(lo, hi, polygon)) return Failure(MapRefinementStatus.CapacityExceeded, "refinement faces or vertices");
                }
            }
            return new(MapRefinementStatus.Complete, Array.AsReadOnly(vertices.ToArray()), faces.AsReadOnly(),
                cellArea, lowerArea, default, Array.Empty<MapLegacyCellTag>(), upper is null ? null : upperArea,
                separation, (int)pairs, null);

            List<Clipped> Clip(IReadOnlyList<MapBoundFace> source)
            {
                var result = new List<Clipped>();
                foreach (MapBoundFace face in source.OrderBy(f => f.Key))
                {
                    List<MapExactXz> polygon = MapRefinementPolygon.Rectangle(MapRefinementPolygon.Triangle(face.Triangle), min, max);
                    MapExactValue area = MapRefinementPolygon.Area(polygon);
                    if (area.Sign > 0) result.Add(new(face, polygon, area, MapRefinementPolygon.Bounds.Of(polygon)));
                }
                return result;
            }

            bool Emit(Clipped lo, Clipped? hi, List<MapExactXz> polygon)
            {
                if (faces.Count >= limits.MaxRefinementFaces) return false;
                foreach (MapExactXz vertex in polygon)
                {
                    if (!vertices.Contains(vertex) && vertices.Count >= limits.MaxRefinementVertices) return false;
                    vertices.Add(vertex);
                }
                MapExactValue? faceSeparation = null;
                if (hi is not null)
                    foreach (MapExactXz vertex in polygon)
                    {
                        MapExactValue delta = Height(hi.Face.Triangle, vertex).Subtract(Height(lo.Face.Triangle, vertex));
                        faceSeparation = Minimum(faceSeparation, delta);
                        separation = Minimum(separation, delta);
                    }
                faces.Add(new(lo.Face.Key, hi?.Face.Key, MapRefinementPolygon.Normalize(polygon), faceSeparation));
                return true;
            }
        }
        catch (MapExactOverflowException) { return Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
    }

    sealed record Clipped(MapBoundFace Face, IReadOnlyList<MapExactXz> Polygon, MapExactValue Area,
        MapRefinementPolygon.Bounds Bounds);
    static MapExactValue Sum(IEnumerable<Clipped> polygons) => polygons.Aggregate(default(MapExactValue), (sum, p) => sum.Add(p.Area));
    static MapExactValue Minimum(MapExactValue? current, MapExactValue value) =>
        current is null || value.CompareTo(current.Value) < 0 ? value : current.Value;
    static MapExactValue Height(MapExactTriangle triangle, MapExactXz vertex) =>
        MapSubdividedTriangle.Height(triangle, vertex.X, vertex.Z) ?? throw new InvalidOperationException("refinement vertex outside source triangle");
    public static MapExactValue Area(MapRefinementFace face) => MapRefinementPolygon.Area(face.Polygon);

    internal static MapCellRefinement Failure(MapRefinementStatus status, string? detail) =>
        new(status, Array.Empty<MapExactXz>(), Array.Empty<MapRefinementFace>(), default, default, default,
            Array.Empty<MapLegacyCellTag>(), null, null, 0, detail);

    public static MapBoundFaceSet BoundFaces(MapScopedSurfaces view, MapSpaceFootprint footprint, MapBoundRef bound,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits)
    {
        try
        {
            var preparation = new MapBoundPreparation(view, limits, null);
            MapBoundPreparation.Plan plan = preparation.Bound(footprint, bound, min, max);
            if (plan.Status != MapRefinementStatus.Complete) return EmptyBound(plan.Status, plan.Detail);
            MapBoundFaceContext? context = MapBoundFaceContext.PrepareBounds(view,
                plan.Demands.Select(d => new MapBoundFaceDemand(d, plan.Opening)),
                limits.MaxContextPatches, limits.MaxContextFaces, null, out string? refusal);
            return context is null ? EmptyBound(MapRefinementStatus.CapacityExceeded, refusal) : ReadBound(context, plan);
        }
        catch (MapExactOverflowException) { return EmptyBound(MapRefinementStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error) { return BoundError(error); }
    }

    internal static MapBoundFaceSet BoundFaces(MapBoundFaceContext context, MapSpaceFootprint footprint, MapBoundRef bound,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits)
    {
        try
        {
            MapBoundPreparation.Plan plan = new MapBoundPreparation(context.View, limits, null).Bound(footprint, bound, min, max);
            return plan.Status == MapRefinementStatus.Complete ? ReadBound(context, plan) : EmptyBound(plan.Status, plan.Detail);
        }
        catch (MapExactOverflowException) { return EmptyBound(MapRefinementStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error) { return BoundError(error); }
    }

    static MapBoundFaceSet ReadBound(MapBoundFaceContext context, MapBoundPreparation.Plan plan)
    {
        if (plan.Demands.Count == 0)
            return new(MapRefinementStatus.Complete, Array.Empty<MapBoundFace>(), plan.Compatibility, null);
        IEnumerable<MapBoundFace> faces = plan.Opening is { } opening
            ? context.Opening(opening).Where(f => plan.Demands.Any(d => d.SlotCell == f.Key.Primitive))
            : plan.Demands.SelectMany(context.Faces);
        return new(MapRefinementStatus.Complete, Array.AsReadOnly(faces.OrderBy(f => f.Key).ToArray()),
            Array.AsReadOnly(plan.Compatibility.OrderBy(c => c.Patch).ThenBy(c => c.SlotCell).ToArray()), null);
    }

    static MapBoundFaceSet EmptyBound(MapRefinementStatus status, string? detail) =>
        new(status, Array.Empty<MapBoundFace>(), Array.Empty<MapLegacyCellTag>(), detail);
    static MapBoundFaceSet BoundError(MapDocumentException error) => EmptyBound(
        NotRepresentable(error) ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.Invalid,
        NotRepresentable(error) ? "overflow: " + error.Message : error.Message);
    internal static bool NotRepresentable(Exception error) =>
        error is MapExactOverflowException or OverflowException || error.InnerException is { } inner && NotRepresentable(inner);

    public static MapCellRefinement RefineFootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint,
        int slotCell, MapRefinementLimits limits) => RefineCell(view, footprint, slotCell, limits, null);
    internal static MapCellRefinement RefineFootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint,
        int slotCell, MapRefinementLimits limits, MapBoundFaceWork work) => RefineCell(view, footprint, slotCell, limits, work);

    static MapCellRefinement RefineCell(MapScopedSurfaces view, MapSpaceFootprint footprint,
        int slotCell, MapRefinementLimits limits, MapBoundFaceWork? work)
    {
        MapFootprintPreparation prepared = Prepare(view, new[] { footprint }, _ => new[] { slotCell }, limits, work);
        if (prepared.CellOutcomes.TryGetValue((footprint.Id, slotCell), out MapCellRefinement? outcome)) return outcome;
        if (prepared.Context is null) return Failure(MapRefinementStatus.CapacityExceeded, prepared.Refusal);
        return RefineFootprintCell(prepared.Context, footprint, slotCell, limits);
    }

    internal static MapFootprintPreparation PrepareFootprints(MapScopedSurfaces view, IReadOnlyList<MapSpaceFootprint> footprints,
        MapRefinementLimits limits, MapBoundFaceWork? work) => Prepare(view, footprints, f => f.SlotCells, limits, work);

    static MapFootprintPreparation Prepare(MapScopedSurfaces view, IReadOnlyList<MapSpaceFootprint> footprints,
        Func<MapSpaceFootprint, IEnumerable<int>> cells, MapRefinementLimits limits, MapBoundFaceWork? work)
    {
        var outcomes = new Dictionary<(string, int), MapCellRefinement>();
        var preparation = new MapBoundPreparation(view, limits, work);
        MapBoundFaceContext? context;
        string? refusal;
        try
        {
            context = MapBoundFaceContext.PrepareBounds(view, Demands(),
                limits.MaxContextPatches, limits.MaxContextFaces, work, out refusal);
        }
        catch (Exception error) when (error is MapExactOverflowException or MapDocumentException)
        {
            MapRefinementStatus status = NotRepresentable(error) ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.Invalid;
            foreach (MapSpaceFootprint footprint in footprints)
                foreach (int cell in cells(footprint))
                    outcomes.TryAdd((footprint.Id, cell), Failure(status, status == MapRefinementStatus.NotRepresentable ? "overflow" : error.Message));
            context = null;
            refusal = null;
        }
        return new(context, refusal, new ReadOnlyDictionary<(string, int), MapCellRefinement>(outcomes));

        IEnumerable<MapBoundFaceDemand> Demands()
        {
            foreach (MapSpaceFootprint footprint in footprints.OrderBy(f => f.Id, StringComparer.Ordinal))
                foreach (int cell in cells(footprint).Order())
                {
                    MapBoundPreparation.Plan? lower = null, upper = null;
                    MapCellRefinement? failure = null;
                    try
                    {
                        MapExactXz[] rect = preparation.Rectangle(footprint, cell);
                        lower = preparation.Bound(footprint, footprint.Lower, rect[0], rect[1]);
                        if (lower.Status != MapRefinementStatus.Complete) failure = Failure(lower.Status, lower.Detail);
                        else if (footprint.Upper.Kind != MapBoundKind.OpenTop)
                        {
                            upper = preparation.Bound(footprint, footprint.Upper, rect[0], rect[1]);
                            if (upper.Status != MapRefinementStatus.Complete) failure = Failure(upper.Status, upper.Detail);
                        }
                    }
                    catch (MapExactOverflowException) { failure = Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
                    catch (MapDocumentException error)
                    {
                        failure = Failure(NotRepresentable(error) ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.Invalid,
                            NotRepresentable(error) ? "overflow: " + error.Message : error.Message);
                    }
                    if (failure is not null) { outcomes[(footprint.Id, cell)] = failure; continue; }
                    foreach (MapCellDemand demand in lower!.Demands) yield return new(demand, lower.Opening);
                    if (upper is not null)
                        foreach (MapCellDemand demand in upper.Demands) yield return new(demand, upper.Opening);
                }
        }
    }

    internal static MapCellRefinement RefineFootprintCell(MapBoundFaceContext context, MapSpaceFootprint footprint,
        int slotCell, MapRefinementLimits limits)
    {
        try
        {
            MapExactXz[] rect = new MapBoundPreparation(context.View, limits, null).Rectangle(footprint, slotCell);
            MapBoundFaceSet lower = BoundFaces(context, footprint, footprint.Lower, rect[0], rect[1], limits);
            if (lower.Status != MapRefinementStatus.Complete) return Failure(lower.Status, lower.Detail);
            MapBoundFaceSet? upper = footprint.Upper.Kind == MapBoundKind.OpenTop ? null
                : BoundFaces(context, footprint, footprint.Upper, rect[0], rect[1], limits);
            if (upper is not null && upper.Status != MapRefinementStatus.Complete) return Failure(upper.Status, upper.Detail);
            MapCellRefinement result = Refine(lower.Faces, upper?.Faces, rect[0], rect[1], limits);
            if (result.Status != MapRefinementStatus.Complete) return result;
            MapExactValue compatibilityArea = default;
            foreach (MapLegacyCellTag tag in lower.Compatibility)
            {
                MapSurfaceRef surface = context.View.Surfaces.Single(s => s.Id == tag.Patch.SurfaceId);
                MapExactXz[] cell = MapLatticeRanges.CellRect(surface.Frame, tag.Patch, tag.SlotCell);
                MapExactValue minX = Max(rect[0].X, cell[0].X), minZ = Max(rect[0].Z, cell[0].Z);
                MapExactValue maxX = Min(rect[1].X, cell[1].X), maxZ = Min(rect[1].Z, cell[1].Z);
                if (minX.CompareTo(maxX) < 0 && minZ.CompareTo(maxZ) < 0)
                    compatibilityArea = compatibilityArea.Add(maxX.Subtract(minX).Multiply(maxZ.Subtract(minZ)));
            }
            return result with { CompatibilityArea = compatibilityArea, CompatibilityCells = lower.Compatibility };
        }
        catch (MapExactOverflowException) { return Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return Failure(NotRepresentable(error) ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.Invalid,
                NotRepresentable(error) ? "overflow: " + error.Message : error.Message);
        }
    }

    internal static MapCellRefinement RefineFootprintCell(MapScopedSurfaces view, MapBoundFaceContext context,
        MapSpaceFootprint footprint, int slotCell, MapRefinementLimits limits)
    {
        context.RequireView(view);
        return RefineFootprintCell(context, footprint, slotCell, limits);
    }
    static MapExactValue Min(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    static MapExactValue Max(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
}
