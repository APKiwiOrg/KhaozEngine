using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

internal readonly record struct MapMembershipObservation(MapMembershipStatus Status, string? SpaceId,
    MapExactPoint ExactPoint, MapReadWitness Witness);

/// <summary>Scoped prepared inputs and bounded mutable work for the shared ordered point kernel.</summary>
internal sealed class MapSpaceMembershipScratch : IMapMembershipPointInput
{
    readonly record struct Active(MapMembershipPreparedFootprint Footprint, MapMembershipBoundPlan Lower, MapMembershipBoundPlan Upper);
    readonly record struct Demand(MapMembershipPreparedGeometry.Cell Cell, int Opening);
    internal long InventoryBytes => checked(256L + _prepared.InventoryBytes + MapMembershipExact.StorageBytes +
        (long)Unsafe.SizeOf<Active>() * _active.Length + (long)Unsafe.SizeOf<Demand>() * _demands.Length +
        8L * (_validated.Length + _physical.Length + _opening.Length) + 8L * _patchFaces.Length +
        MapMembershipPointScratch.Bytes(_scratch.Raw.Length, _scratch.Prisms.Length));
    internal long SharedBytes => _prepared.SharedBytes;
    readonly MapMembershipPreparedView _prepared;
    MapScopedSurfaces _view => _prepared.View;
    MapMembershipPreparedGeometry _geometry => _prepared.Geometry;
    MapMembershipPreparedFootprint[] _footprints => _prepared.Footprints;
    MapMembershipPreparedPortal[] _portals => _prepared.Portals;
    Dictionary<string, MapSpaceDoc> _spaces => _prepared.Spaces;
    Dictionary<string, string> _owners => _prepared.Owners;
    Dictionary<string, MapSpaceDoc[]> _ancestors => _prepared.Ancestors;
    MapMembershipPreparedView.Prism[] _prisms => _prepared.Pieces;
    readonly Active[] _active;
    readonly Demand[] _demands;
    readonly long[] _validated, _physical, _opening;
    long _generation;
    readonly long[] _patchFaces;
    readonly MapMembershipPointScratch _scratch;
    readonly MapMembershipExact _exact = new();
    long _prismVertices => _prepared.PrismVertices;
    string? _roomFailure => _prepared.RoomFailure;
    MapExactXz _point;
    MapExactValue _y;
    int _activeCount, _demandCount, _validatedCount;
    MapMembershipStatus _status;
    string? _space;
    MapExactPoint _world;

    internal MapSpaceMembershipScratch(MapScopedSurfaces view, MapMembershipPreparation demand, MapMembershipDemandPlan? plan = null)
    {
        _prepared = new(view, plan);
        _active = new Active[_footprints.Length];
        _demands = new Demand[checked(2 * _footprints.Length + 16 * _portals.Length + view.Surfaces.Count)];
        // Selected cell and face-set counts are known only after preparation, not in the input header.
        MapMembershipBuild.Inventory(checked(8L * (2L * _geometry.PatchCount + _geometry.Cells.Length + _geometry.FaceSets.Count)));
        _validated = new long[_geometry.PatchCount]; _physical = new long[_geometry.Cells.Length];
        _opening = new long[_geometry.FaceSets.Count]; _patchFaces = new long[_geometry.PatchCount];
        _scratch = new(demand.Parts, Prisms.Length);
    }
    public MapPrismDomain[] Prisms => _prepared.Prisms;
    public int CandidateCount => _activeCount;
    public int PortalCount => _portals.Length;
    public int MaxDepth => _view.Scope.Limits.MaxRecordDepth;
    public bool PruneAncestors => _view.EffectiveContext is not null;
    public MapMembershipStatus FailureStatus => _status;

    internal MapMembershipObservation Query(MapScopedSurfaces view, MapFramePoint point)
    {
        if (!ReferenceEquals(view, _view)) throw new ArgumentException("scratch view does not match", nameof(view));
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z))
            throw new ArgumentException("point must be finite", nameof(point));
        _status = MapMembershipStatus.Outside; _space = null; _world = default;
        try
        {
            _exact.NotRepresentable = false;
            if (!MapMembershipCoordinates.Observer(point, out _world)) return Result(MapMembershipStatus.NotRepresentable);
            _point = new(_world.X, _world.Z); _y = _world.Y;
            var scope = _view.Scope;
            var rectangle = scope.WorldRectangle();
            if (_world.X.CompareTo(rectangle.MinX) < 0 || _world.X.CompareTo(rectangle.MaxX) > 0 ||
                _world.Z.CompareTo(rectangle.MinZ) < 0 || _world.Z.CompareTo(rectangle.MaxZ) > 0 ||
                point.Local.Y < scope.MinY || point.Local.Y > scope.MaxY)
                return Result(MapMembershipStatus.MissingGeometry);
            if (_view.Status == MapAcquireStatus.CapacityExceeded) return Result(MapMembershipStatus.CapacityExceeded);
            if (_view.Status == MapAcquireStatus.NotRepresentable) return Result(MapMembershipStatus.NotRepresentable);
            if (_view.EffectiveContext is not null && _view.Status == MapAcquireStatus.Incomplete) return Result(MapMembershipStatus.MissingGeometry);
            if (_roomFailure is not null) return Result(MapMembershipStatus.MissingGeometry);
            if (Prisms.Length > scope.Limits.MaxRecordReads || _prismVertices > scope.Limits.MaxInspectedFaces)
                return Result(MapMembershipStatus.CapacityExceeded);
            if (Prisms.Length != 0 && _view.Status == MapAcquireStatus.Incomplete) return Result(MapMembershipStatus.MissingGeometry);
            if (++_generation == long.MaxValue)
            {
                Array.Clear(_validated); Array.Clear(_physical); Array.Clear(_opening); _generation = 1;
            }
            Array.Clear(_active, 0, _activeCount); Array.Clear(_demands, 0, _demandCount);
            _activeCount = 0; _demandCount = 0; _validatedCount = 0;
            for (int i = 0; i < _footprints.Length; i++)
            {
                var footprint = _footprints[i];
                if (!footprint.Contains(_point, _exact)) continue;
                var lower = footprint.Lower.Plan(_point, _exact);
                if (!ValidatePlan(lower)) return Result(_status);
                var upper = footprint.Upper.Plan(_point, _exact);
                if (!ValidatePlan(upper)) return Result(_status);
                _active[_activeCount++] = new(footprint, lower, upper);
                Add(lower); Add(upper);
            }
            for (int i = 0; i < _portals.Length; i++)
            {
                var portal = _portals[i];
                if (!portal.Incident(_point, _exact) || portal.Ignored) continue;
                var bottom = portal.Bottom(_point, _exact);
                if (bottom is null) return Result(MapMembershipStatus.Invalid);
                if (!AddChain(bottom)) return Result(_status);
                if (portal.Record.BandTop is not null)
                {
                    var top = portal.Top(_point, _exact);
                    if (top is null) return Result(MapMembershipStatus.Invalid);
                    if (!AddChain(top)) return Result(_status);
                }
            }
            for (int i = 0; i < _view.Surfaces.Count; i++)
            {
                var surface = _view.Surfaces[i];
                if (surface.IndoorSpan is null) continue;
                var cell = _geometry.Surfaces[surface.Id].Find(_point, _exact);
                if (cell is { } found && found.Indoor) Queue(found, -1);
            }
            long faces = 0, remaining = scope.Limits.MaxInspectedFaces - _prismVertices;
            for (int i = 0; i < _demandCount; i++)
            {
                var demand = _demands[i]; var cell = demand.Cell;
                if (!ValidatePatch(cell.PatchIndex)) return Result(_status);
                if (_physical[cell.Id] != _generation)
                {
                    _physical[cell.Id] = _generation; faces += cell.PhysicalFaces;
                    _patchFaces[cell.PatchIndex] += cell.PhysicalFaces;
                }
                if (demand.Opening >= 0 && _opening[demand.Opening] != _generation)
                { _opening[demand.Opening] = _generation; faces += _geometry.FaceSets[demand.Opening].Length; }
                if (faces > remaining || _patchFaces[cell.PatchIndex] > MapSurfaceCompiler.MaxFacesPerPatch)
                    return Result(MapMembershipStatus.CapacityExceeded);
            }
            _status = MapMembershipStatus.Resolved;
            MapMembershipPointKernel.Query(this, _y, _scratch);
            if (_scratch.Status != MapMembershipStatus.Resolved)
                return Result(_scratch.Status == MapMembershipStatus.Outside && _view.Status == MapAcquireStatus.Incomplete
                    ? MapMembershipStatus.MissingGeometry : _scratch.Status);
            var picked = _scratch.Selected!.Value;
            var selected = Target(picked.Space);
            var ancestry = _ancestors[selected.Id];
            for (int i = 0; i < _view.Surfaces.Count; i++)
            {
                var surface = _view.Surfaces[i];
                if (surface.IndoorSpan is not { } span) continue;
                bool parent = false;
                for (int j = 0; j < ancestry.Length; j++) parent |= ancestry[j].Id == span.ParentSpace.Id;
                if (!parent) continue;
                var cell = _geometry.Surfaces[surface.Id].Find(_point, _exact);
                if (cell is not { } found || !found.Indoor) continue;
                var lower = MapMembershipPreparedGeometry.Height(found.SpanLower, _point, _exact);
                var upper = MapMembershipPreparedGeometry.Height(found.SpanUpper, _point, _exact);
                if (lower is null || upper is null) return Result(_exact.NotRepresentable ? MapMembershipStatus.NotRepresentable : MapMembershipStatus.MissingGeometry);
            }
            _space = selected.Id;
            return Result(MapMembershipStatus.Resolved);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Result(MapMembershipStatus.NotRepresentable); }
        catch (MapDocumentException error)
        {
            return Result(error.Message.Contains("capacity exceeded", StringComparison.Ordinal) || error.Message.Contains("domain work exceeds", StringComparison.Ordinal)
                ? MapMembershipStatus.CapacityExceeded : MapCommonRefinement.NotRepresentable(error) || error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase)
                    ? MapMembershipStatus.NotRepresentable : error.Message.Contains("invalid", StringComparison.Ordinal) || error.Message.Contains("legacy recipe", StringComparison.Ordinal)
                        ? MapMembershipStatus.Invalid : MapMembershipStatus.MissingGeometry);
        }
    }
    MapMembershipObservation Result(MapMembershipStatus status)
    {
        _status = status;
        return new(status, status == MapMembershipStatus.Resolved ? _space : null, _world, _view.ReadWitness);
    }
    bool ValidatePatch(int index)
    {
        if (_validated[index] == _generation) return true;
        if (_validatedCount >= _view.Scope.Limits.MaxCandidatePatches)
        { _status = MapMembershipStatus.CapacityExceeded; return false; }
        _validated[index] = _generation; _patchFaces[index] = 0; _validatedCount++; return true;
    }
    bool ValidatePlan(MapMembershipBoundPlan plan)
    {
        if (plan.ValidatesPatch && plan.Cell is { } cell && !ValidatePatch(cell.PatchIndex)) return false;
        if (plan.Result.Status == MapMembershipStatus.Resolved) return true;
        _status = plan.Result.Status; return false;
    }
    void Add(MapMembershipBoundPlan plan)
    {
        if (plan.Cell is { } cell && plan.Result.Compatibility is null)
            Queue(cell, plan.Result.Opening is null ? -1 : plan.Faces);
    }
    bool AddChain(MapMembershipPreparedPortal.Segment segment)
    {
        for (int i = 0; i < segment.Demands.Length; i++)
        {
            var cell = segment.Demands[i];
            if (!ValidatePatch(cell.PatchIndex)) return false;
            Queue(cell, -1);
        }
        return true;
    }
    void Queue(MapMembershipPreparedGeometry.Cell cell, int opening)
    {
        if (_demandCount == _demands.Length) throw new InvalidOperationException("Admitted membership demands exceeded.");
        _demands[_demandCount++] = new(cell, opening);
    }
    MapSpaceDoc Target(MapSpaceDoc space) => space.AliasOf is { } alias ? _spaces[alias.Id] : space;
    public string Owner(MapSpaceDoc space) => _owners[space.Id];
    public IReadOnlyList<MapSpaceDoc> Ancestors(MapSpaceDoc space) => _ancestors[space.Id];
    public bool Ancestor(string ancestor, string child)
    {
        var ancestry = _ancestors[child];
        for (int i = 1; i < ancestry.Length; i++) if (_owners[ancestry[i].Id] == ancestor) return true;
        return false;
    }
    public MapMembershipPart? Candidate(int index)
    {
        var active = _active[index];
        var lower = active.Lower.Height(_point, _geometry, _exact);
        var upper = active.Upper.Height(_point, _geometry, _exact);
        if (lower is null || active.Footprint.Record.Upper.Kind != MapBoundKind.OpenTop && upper is null)
        { _status = _exact.NotRepresentable ? MapMembershipStatus.NotRepresentable : MapMembershipStatus.MissingGeometry; return null; }
        var space = Target(_spaces[active.Footprint.Record.Space.Id]);
        return new(space, _owners[space.Id], lower.Value, upper, LowerSource: active.Lower.Faces,
            UpperSource: active.Upper.Faces, Compatibility: Prisms.Length == 0 ? active.Lower.Result.Compatibility : null, OccupiedSpaceId: Target(space).Id);
    }
    public MapMembershipPart? Portal(int index)
    {
        var portal = _portals[index];
        if (portal.Ignored || !portal.Incident(_point, _exact)) return null;
        var bottom = portal.Bottom(_point, _exact)!.Height(_point, _exact);
        MapExactValue? top = portal.Record.BandTop is null ? null : portal.Top(_point, _exact)!.Height(_point, _exact);
        if (bottom is null || portal.Record.BandTop is not null && top is null)
        { _status = _exact.NotRepresentable ? MapMembershipStatus.NotRepresentable : MapMembershipStatus.Invalid; return null; }
        if (_y.CompareTo(bottom.Value) < 0 || top is { } high && _y.CompareTo(high) >= 0) return null;
        var space = _spaces[portal.Record.ToSpace.Id];
        return new(space, Target(space).Id, bottom.Value, top);
    }
    public void ValidatePrism(int index) { }
    public string PrismOwner(int index) => _prisms[index].Owner;
    public string PrismParent(int index) => _prisms[index].Parent;
    public bool PrismContains(int index)
    {
        var rings = _prisms[index].Rings;
        for (int i = 0; i < rings.Length; i++) if (rings[i].Contains(_point, _exact)) return true;
        return false;
    }
    public (MapExactValue Lower, MapExactValue Upper) PrismHeights(int index) => (_prisms[index].Lower, _prisms[index].Upper);
    public MapMembershipPart Child(int index, int parentIndex, MapMembershipPart parent, MapExactValue lower,
        MapExactValue upper, bool parentLow, bool parentHigh) => new(_prisms[index].Space, _prisms[index].Owner,
            lower, upper, LowerSource: parentLow ? parent.LowerSource : -1,
            UpperSource: parentHigh ? parent.UpperSource : -1, Parent: parentIndex, Prism: index);
    public int Direction(MapMembershipPart part, bool upper)
    {
        int index = upper ? part.UpperSource : part.LowerSource;
        return index < 0 ? 0 : MapMembershipPreparedGeometry.Direction(_geometry.FaceSets[index], _point, _exact);
    }
    public MapMembershipPart Carve(MapMembershipPart part, MapExactValue lower, MapExactValue? upper,
        MapMembershipPart? lowCut, MapMembershipPart? highCut) => part with { Lower = lower, Upper = upper };
}
