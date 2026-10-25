using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

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
        if (min.X.CompareTo(max.X) >= 0 || min.Z.CompareTo(max.Z) >= 0) return Failure(MapRefinementStatus.Invalid, "cell");
        return MapEffectiveGeometry.RefinePolygon(lower, upper, new[] { min, new(min.X, max.Z), max, new(max.X, min.Z) }, limits, work);
    }

    internal static MapWideCellRefinement RefineWide(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper,
        IReadOnlyList<MapWideXz> polygon, MapRefinementLimits limits, MapRefinementWork? work = null, MapWaterWorkBudget? budget = null) =>
        MapEffectiveGeometry.RefinePolygonWide(lower, upper, polygon, limits, work, budget);

    public static MapExactValue Area(MapRefinementFace face) => MapEffectiveGeometry.Area(face.Polygon);

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
                limits.MaxContextPatches, limits.MaxContextFaces, null, out string? refusal, preparation);
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
            MapBoundPreparation.Plan plan = new MapBoundPreparation(context, limits).Bound(footprint, bound, min, max);
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
            ? plan.Demands.SelectMany(d => context.Opening(opening, d))
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
        MapRefinementLimits limits, MapBoundFaceWork? work, MapWaterWorkMeter? meter = null) => Prepare(view, footprints, f => f.SlotCells, limits, work, meter);

    internal static MapFootprintPreparation Prepare(MapScopedSurfaces view, IReadOnlyList<MapSpaceFootprint> footprints,
        Func<MapSpaceFootprint, IEnumerable<int>> cells, MapRefinementLimits limits, MapBoundFaceWork? work, MapWaterWorkMeter? meter = null,
        MapBoundPreparation? preparation = null)
    {
        var outcomes = new Dictionary<(string, int), MapCellRefinement>();
        preparation ??= new MapBoundPreparation(view, limits, work, meter);
        MapBoundFaceContext? context;
        string? refusal;
        try
        {
            context = MapBoundFaceContext.PrepareBounds(view, Demands(),
                limits.MaxContextPatches, limits.MaxContextFaces, work, out refusal, preparation);
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
        int slotCell, MapRefinementLimits limits) => RefineFootprintCellWide(context, footprint, slotCell, limits).Narrow();

    internal static MapWideCellRefinement RefineFootprintCellWide(MapBoundFaceContext context, MapSpaceFootprint footprint,
        int slotCell, MapRefinementLimits limits)
    {
        try
        {
            MapExactXz[] rect = new MapBoundPreparation(context, limits).Rectangle(footprint, slotCell);
            MapBoundFaceSet lower = BoundFaces(context, footprint, footprint.Lower, rect[0], rect[1], limits);
            if (lower.Status != MapRefinementStatus.Complete) return MapWideCellRefinement.Failure(lower.Status, lower.Detail);
            MapBoundFaceSet? upper = footprint.Upper.Kind == MapBoundKind.OpenTop ? null
                : BoundFaces(context, footprint, footprint.Upper, rect[0], rect[1], limits);
            if (upper is not null && upper.Status != MapRefinementStatus.Complete) return MapWideCellRefinement.Failure(upper.Status, upper.Detail);
            IReadOnlyList<MapWideXz> polygon = context.View.EffectiveContext is null
                ? new[] { MapWideXz.From(rect[0]), MapWideXz.From(new(rect[0].X, rect[1].Z)), MapWideXz.From(rect[1]), MapWideXz.From(new(rect[1].X, rect[0].Z)) }
                : MapEffectiveGeometry.CellPolygon(context.View, footprint.Lattice, slotCell).Select(MapWideXz.From).ToArray();
            var result = RefineWide(lower.Faces, upper?.Faces, polygon, limits);
            if (result.Status != MapRefinementStatus.Complete) return result;
            MapWideRational compatibilityArea = default;
            foreach (MapLegacyCellTag tag in lower.Compatibility)
            {
                MapSurfaceRef surface = context.View.Surfaces.Single(s => s.Id == tag.Patch.SurfaceId);
                MapExactXz[] cell = MapLatticeRanges.CellRect(surface.Frame, tag.Patch, tag.SlotCell);
                MapExactValue minX = Max(rect[0].X, cell[0].X), minZ = Max(rect[0].Z, cell[0].Z);
                MapExactValue maxX = Min(rect[1].X, cell[1].X), maxZ = Min(rect[1].Z, cell[1].Z);
                if (minX.CompareTo(maxX) < 0 && minZ.CompareTo(maxZ) < 0)
                    compatibilityArea = compatibilityArea.Add(MapWideRational.From(maxX).Subtract(MapWideRational.From(minX)).Multiply(MapWideRational.From(maxZ).Subtract(MapWideRational.From(minZ))));
            }
            return result with { CompatibilityArea = compatibilityArea, CompatibilityCells = lower.Compatibility };
        }
        catch (MapExactOverflowException) { return MapWideCellRefinement.Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return MapWideCellRefinement.Failure(NotRepresentable(error) ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.Invalid,
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
