using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The water adapter of Task 4's canonical wide occupied partition. It creates no occupancy algorithm.</summary>
internal static class MapWaterOccupiedPieces
{
    internal static IReadOnlyList<MapOccupiedPiece> Of(MapScopedSurfaces view, string spaceId, MapExactXz min,
        MapExactXz max, MapRefinementLimits limits) => MapOccupiedPartitions.Pieces(view, spaceId, min, max, limits);

    internal static IReadOnlyList<MapOccupiedPiece> Of(MapScopedSurfaces view, MapExactXz min, MapExactXz max,
        MapRefinementLimits limits, MapWaterWorkMeter meter)
    {
        if (view.Status == MapAcquireStatus.CapacityExceeded)
            throw new MapWaterCapacityException("occupied water acquisition capacity exceeded: " + view.Detail);
        if (view.Status == MapAcquireStatus.NotRepresentable)
            throw new MapDocumentException("not representable: occupied water acquisition " + view.Detail);
        // The captured declarations bound candidate work even when an anchor is unavailable. They may preflight
        // work, but never supply geometry to a refused acquisition or make its missing facts complete.
        var declared = view.EffectiveContext?.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapSpaceFootprint>()
            ?? MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>();
        foreach (var footprint in declared)
        {
            meter.ChargeFootprintCells(footprint.SlotCells.Count);
            if (view.Surfaces.Any(s => s.Id == footprint.Lattice.SurfaceId))
                MapWaterBoundReads.ChargeTraversal(view, footprint.Lattice.SurfaceId, min, max, meter);
        }
        if (!view.Witness.Complete)
        {
            var missing = view.Witness.Unavailable.FirstOrDefault();
            throw new MapDocumentException(missing is null ? "missing geometry: occupied water acquisition " + view.Detail
                : $"missing geometry: {missing.What} {missing.Key} ({missing.Status})");
        }
        if (!MapWaterFootprints.HoldsEverything(view) || view.Scope.SpaceIds is not null || !MapWaterFootprints.ScopeHolds(view, min, max))
            throw new MapDocumentException("missing geometry: occupied water window is not proven complete in this view");
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        var result = MapOccupiedPartitions.BuildWide(view, min, max, limits, carve: true, meter);
        if (result.Status != MapRefinementStatus.Complete) throw MapOccupiedPartitions.Refusal(result.Status, result.Detail);
        preparation?.Complete(new MapWaterInventory().Pieces(result.Pieces));
        return result.Pieces;
    }

    internal static MapBox3 Bounds(IReadOnlyList<MapWideXz> polygon, MapWideRational lower, MapWideRational upper) => new(
        Outward(polygon.Min(p => p.X), true), Outward(lower, true), Outward(polygon.Min(p => p.Z), true),
        Outward(polygon.Max(p => p.X), false), Outward(upper, false), Outward(polygon.Max(p => p.Z), false));

    static double Outward(MapWideRational value, bool lower)
    {
        double result = value.ToDouble();
        int comparison = MapWideRational.FromDouble(result).CompareTo(value);
        return lower && comparison > 0 ? Math.BitDecrement(result) : !lower && comparison < 0 ? Math.BitIncrement(result) : result;
    }
}
