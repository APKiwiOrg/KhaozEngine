using System;
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
    readonly MapMembershipPreparedGeometry.Surface? _surface;
    readonly MapMembershipBoundPlan[] _plans;
    readonly MapMembershipBoundPlan _absent;
    internal MapMembershipPreparedBound(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry,
        MapSpaceFootprint footprint, MapBoundRef bound)
    {
        var bounds = new MapSpacePointBounds(view, new(MaxContextPatches: geometry.PatchCount + 1), null);
        if (bound.Kind == MapBoundKind.OpenTop)
        {
            _plans = Array.Empty<MapMembershipBoundPlan>();
            _absent = new(new(MapMembershipStatus.Resolved, null, null, null, null), null, -1);
            return;
        }
        string? id = bound.SurfaceId;
        if (bound.Opening is { } reference && view.TryRecord(reference, out var record, out _, borrow: true) && record is MapHorizontalOpening opening)
            id = opening.Patch.SurfaceId;
        if (id is not null) geometry.Surfaces.TryGetValue(id, out _surface);
        _plans = new MapMembershipBoundPlan[geometry.Cells.Length];
        _absent = new(new(MapMembershipStatus.MissingGeometry, null, null, null, "missing bound at exact XZ"), null, -1);
        if (_surface is null)
        {
            if (bound == footprint.Lower && bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1)
                _absent = new(bounds.Plan(footprint, bound, default), null, -1);
            else if (bound.Kind == MapBoundKind.LegacyExteriorV1)
                _absent = new(new(MapMembershipStatus.Invalid, null, null, null, "legacy recipe: upper"), null, -1);
            return;
        }
        for (int i = 0; i < _surface.Cells.Length; i++)
        {
            var cell = _surface.Cells[i];
            var result = bounds.Plan(footprint, bound, cell.Centre);
            int faces = result.Opening is { } source && geometry.OpeningSets.TryGetValue(source, out var sets) &&
                sets.TryGetValue(cell.Demand.SlotCell, out int openingFaces) ? openingFaces : cell.FaceSet;
            bool validates = bound == footprint.Lower ? result.Status == MapMembershipStatus.Resolved :
                result.Status != MapMembershipStatus.Invalid && bound.Kind is MapBoundKind.Ceiling or MapBoundKind.HorizontalOpening;
            _plans[cell.Id] = new(result, cell, faces, validates);
        }
    }
    internal MapMembershipBoundPlan Plan(MapExactXz point, MapMembershipExact exact)
    {
        var cell = _surface?.Find(point, exact);
        return cell is null ? _absent : _plans[cell.Id];
    }
}

internal sealed class MapMembershipPreparedFootprint
{
    internal readonly MapSpaceFootprint Record;
    internal readonly MapMembershipPreparedBound Lower, Upper;
    readonly MapMembershipPolygon[] _cells;
    internal MapMembershipPreparedFootprint(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry, MapSpaceFootprint record)
    {
        Record = record;
        _cells = new MapMembershipPolygon[record.SlotCells.Count];
        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = new(MapEffectiveGeometry.CellPolygon(view, record.Lattice, record.SlotCells[i]), cell: true);
        Lower = new(view, geometry, record, record.Lower); Upper = new(view, geometry, record, record.Upper);
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < _cells.Length; i++) if (_cells[i].Contains(point, exact)) return true;
        return false;
    }
}
