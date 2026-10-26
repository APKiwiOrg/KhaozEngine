using System;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Released bound planning frozen per acquired cell, with only exact owner lookup at query time.</summary>
internal sealed class MapMembershipPreparedBound
{
    internal long InventoryBytes => checked(128L + 8L * _plans.Length);
    readonly MapMembershipPreparedGeometry.Surface? _surface;
    readonly int[] _plans;
    readonly MapMembershipFrozenBoundStore _store;
    readonly int[] _ids;
    readonly MapMembershipBoundPlan _absent;
    internal MapMembershipPreparedBound(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry,
        MapSpaceFootprint footprint, MapBoundRef bound, MapMembershipDemandPlan plan)
    {
        MapMembershipBuild.Inventory(checked(128L + (bound.Kind == MapBoundKind.OpenTop ? 0 : 8L * plan.Bounds[(footprint.Id, bound == footprint.Upper)].Length)));
        using var planning = MapMembershipBuild.Kernel();
        int count = bound.Kind == MapBoundKind.OpenTop ? 0 : plan.Bounds[(footprint.Id, bound == footprint.Upper)].Length;
        _plans = new int[count]; _ids = new int[count]; _store = geometry.Bounds;
        var bounds = new MapSpacePointBounds(view, new(MaxContextPatches: geometry.PatchCount + 1), null);
        if (bound.Kind == MapBoundKind.OpenTop)
        {
            _absent = _store.Add(new(MapMembershipStatus.Resolved, null, null, null, null), null, -1);
            return;
        }
        string? id = bound.SurfaceId;
        if (bound.Opening is { } reference && view.TryRecord(reference, out var record, out _, borrow: true) && record is MapHorizontalOpening opening)
            id = opening.Patch.SurfaceId;
        if (id is not null) geometry.Surfaces.TryGetValue(id, out _surface);
        MapSpacePointBounds.Bound absent = new(MapMembershipStatus.MissingGeometry, null, null, null, "missing bound at exact XZ");
        if (_surface is null && bound == footprint.Lower && bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1)
            absent = bounds.Plan(footprint, bound, default);
        else if (_surface is null && bound.Kind == MapBoundKind.LegacyExteriorV1)
            absent = new(MapMembershipStatus.Invalid, null, null, null, "legacy recipe: upper");
        _absent = _store.Add(absent, null, -1);
        if (_surface is null) return;
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
            _ids[index] = cell.Id; _plans[index++] = _store.Add(result, cell, faces, validates).Index;
        }
        Array.Sort(_ids, _plans);
    }
    internal MapMembershipBoundPlan Plan(MapExactXz point, MapMembershipExact exact)
    {
        var cell = _surface?.Find(point, exact);
        int index = cell is { } found ? Array.BinarySearch(_ids, found.Id) : -1;
        return index < 0 ? _absent : new(_store, _plans[index]);
    }
}

internal sealed class MapMembershipPreparedFootprint
{
    internal long InventoryBytes => checked(128L + (_arenaOwner?.InventoryBytes ?? 0) + _sharingControl + _sharedBytes + Lower.InventoryBytes + Upper.InventoryBytes);
    internal readonly MapSpaceFootprint Record;
    internal readonly MapMembershipPreparedBound Lower, Upper;
    readonly MapMembershipGeometryArena? _arenaOwner;
    readonly long _sharedBytes, _sharingControl;
    internal long SharedBytes => _sharedBytes;
    readonly MapMembershipPolygonRange _cells;
    internal MapMembershipPreparedFootprint(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry, MapSpaceFootprint record, MapMembershipDemandPlan plan)
    {
        MapMembershipBuild.Inventory(128);
        Record = record;
        if (MapMembershipBuild.Current is { Scene: { } scene } build && ReferenceEquals(scene.SurfaceView, view))
        {
            // Consumer lease and dependency rows are private, even when the physical geometry is shared.
            MapMembershipBuild.Inventory(512); _sharingControl = 512;
            var shared = MapMembershipProductCache.Acquire(build, record);
            _cells = shared.Geometry; _sharedBytes = shared.SharedBytes;
        }
        else
        {
            using var building = MapMembershipArenaBuilder.Begin();
            _arenaOwner = building.Owns ? building.Arena : null;
            int first = building.Arena.Polygons.Count;
            foreach (int slot in record.SlotCells)
                _ = new MapMembershipPolygon(MapEffectiveGeometry.CellPolygon(view, record.Lattice, slot), cell: true);
            _cells = new(building.Arena, first, record.SlotCells.Count);
            if (MapMembershipBuild.Current is { } own) own.Counts.UniqueFootprintPolygons += _cells.Length;
        }
        Lower = new(view, geometry, record, record.Lower, plan); Upper = new(view, geometry, record, record.Upper, plan);
    }
    internal bool Contains(MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < _cells.Length; i++) if (_cells[i].Contains(point, exact)) return true;
        return false;
    }
}
