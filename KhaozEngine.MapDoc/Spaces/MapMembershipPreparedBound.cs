using System;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

internal readonly record struct MapMembershipBoundPlan(MapSpacePointBounds.Bound Result,
    MapMembershipPreparedGeometry.Cell? Cell, int Faces, bool ValidatesPatch = false)
{
    internal MapExactValue? Height(MapExactXz point, MapMembershipPreparedGeometry geometry, MapMembershipExact exact)
    {
        if (Cell is null) return null;
        return Result.Compatibility is not null ? Cell.LegacyHeight(point, exact)
            : MapMembershipPreparedGeometry.Height(geometry.FaceSets[Faces], point, exact);
    }
}

/// <summary>Released bound planning frozen per acquired cell, with only exact owner lookup at query time.</summary>
internal sealed class MapMembershipPreparedBound
{
    internal long InventoryBytes => checked(128L + 192L * _plans.Length);
    readonly MapMembershipPreparedGeometry.Surface? _surface;
    readonly MapMembershipBoundPlan[] _plans;
    readonly int[] _ids;
    readonly MapMembershipBoundPlan _absent;
    internal MapMembershipPreparedBound(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry,
        MapSpaceFootprint footprint, MapBoundRef bound, MapMembershipDemandPlan plan)
    {
        MapMembershipBuild.Inventory(checked(128L + (bound.Kind == MapBoundKind.OpenTop ? 0 : 192L * plan.Bounds[(footprint.Id, bound == footprint.Upper)].Length)));
        using var planning = MapMembershipBuild.Kernel();
        int count = bound.Kind == MapBoundKind.OpenTop ? 0 : plan.Bounds[(footprint.Id, bound == footprint.Upper)].Length;
        _plans = new MapMembershipBoundPlan[count]; _ids = new int[count];
        var bounds = new MapSpacePointBounds(view, new(MaxContextPatches: geometry.PatchCount + 1), null);
        if (bound.Kind == MapBoundKind.OpenTop)
        {
            _absent = new(new(MapMembershipStatus.Resolved, null, null, null, null), null, -1);
            return;
        }
        string? id = bound.SurfaceId;
        if (bound.Opening is { } reference && view.TryRecord(reference, out var record, out _, borrow: true) && record is MapHorizontalOpening opening)
            id = opening.Patch.SurfaceId;
        if (id is not null) geometry.Surfaces.TryGetValue(id, out _surface);
        _absent = new(new(MapMembershipStatus.MissingGeometry, null, null, null, "missing bound at exact XZ"), null, -1);
        if (_surface is null)
        {
            if (bound == footprint.Lower && bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1)
                _absent = new(bounds.Plan(footprint, bound, default), null, -1);
            else if (bound.Kind == MapBoundKind.LegacyExteriorV1)
                _absent = new(new(MapMembershipStatus.Invalid, null, null, null, "legacy recipe: upper"), null, -1);
            return;
        }
        int index = 0;
        foreach (var demand in plan.Bounds[(footprint.Id, bound == footprint.Upper)])
        {
            var cell = geometry.ByCell[demand];
            var result = bounds.Plan(footprint, bound, cell.Centre);
            if (MapMembershipBuild.Current is { } build && result.Status != MapMembershipStatus.Resolved) build.Counts.FailedBoundAssociations++;
            int faces = result.Opening is { } source && geometry.OpeningSets.TryGetValue(source, out var sets) &&
                sets.TryGetValue(cell.Demand.SlotCell, out int openingFaces) ? openingFaces : cell.FaceSet;
            bool validates = bound == footprint.Lower ? result.Status == MapMembershipStatus.Resolved :
                result.Status != MapMembershipStatus.Invalid && bound.Kind is MapBoundKind.Ceiling or MapBoundKind.HorizontalOpening;
            _ids[index] = cell.Id; _plans[index++] = new(result, cell, faces, validates);
        }
        Array.Sort(_ids, _plans);
    }
    internal MapMembershipBoundPlan Plan(MapExactXz point, MapMembershipExact exact)
    {
        var cell = _surface?.Find(point, exact);
        int index = cell is null ? -1 : Array.BinarySearch(_ids, cell.Id);
        return index < 0 ? _absent : _plans[index];
    }
}

internal sealed class MapMembershipPreparedFootprint
{
    internal long InventoryBytes => checked(128L + _cells.Sum(c => 8L + c.InventoryBytes) + Lower.InventoryBytes + Upper.InventoryBytes);
    internal readonly MapSpaceFootprint Record;
    internal readonly MapMembershipPreparedBound Lower, Upper;
    readonly MapMembershipPolygon[] _cells;
    internal MapMembershipPreparedFootprint(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry, MapSpaceFootprint record, MapMembershipDemandPlan plan)
    {
        MapMembershipBuild.Inventory(checked(128L + 8L * record.SlotCells.Count));
        Record = record;
        _cells = new MapMembershipPolygon[record.SlotCells.Count];
        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = new(MapEffectiveGeometry.CellPolygon(view, record.Lattice, record.SlotCells[i]), cell: true);
        if (MapMembershipBuild.Current is { } build) build.Counts.UniqueFootprintPolygons += _cells.Length;
        Lower = new(view, geometry, record, record.Lower, plan); Upper = new(view, geometry, record, record.Upper, plan);
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < _cells.Length; i++) if (_cells[i].Contains(point, exact)) return true;
        return false;
    }
}
