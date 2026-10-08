using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

public enum MapMembershipStatus { Resolved, Outside, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>Occupied-space facts tied to the same immutable acquisition as the query.</summary>
public sealed class MapMembershipResult
{
    public MapMembershipStatus Status { get; }
    public MapFramePoint Point { get; }
    public string? SpaceId { get; }
    public IReadOnlyList<string> DomainKeys { get; }
    public MapLegacyCellTag? LowerCompatibility { get; }
    public string? Detail { get; }
    public MapReadWitness Witness { get; }

    internal MapMembershipResult(MapMembershipStatus status, MapFramePoint point, string? spaceId,
        IEnumerable<string> domainKeys, MapLegacyCellTag? lowerCompatibility, string? detail, MapReadWitness witness)
    {
        Status = status;
        Point = point;
        SpaceId = spaceId;
        DomainKeys = Array.AsReadOnly(domainKeys.ToArray());
        LowerCompatibility = lowerCompatibility;
        Detail = detail;
        Witness = witness;
    }
}

/// <summary>Resolves membership using only acquired records and one point-cell compilation context.</summary>
public sealed class MapSpaceMembership
{
    readonly MapScopedSurfaces _view;
    public MapSpaceMembership(MapScopedSurfaces scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        _view = scoped;
    }

    public MapMembershipResult Query(MapFramePoint point) => QueryCore(point, null, null);
    internal MapMembershipResult Query(MapFramePoint point, MapBoundFaceWork work) => QueryCore(point, work, null);
    internal MapMembershipResult Query(MapFramePoint point, MapBoundFaceContext context)
    {
        context.RequireView(_view);
        return QueryCore(point, null, context);
    }

    sealed record Candidate(MapSpaceFootprint Footprint, MapSpacePointBounds.Bound Lower, MapSpacePointBounds.Bound Upper);

    MapMembershipResult QueryCore(MapFramePoint point, MapBoundFaceWork? work, MapBoundFaceContext? supplied)
    {
        if (point.Frame != _view.Scope.Frame) throw new ArgumentException("point frame differs from acquired frame", nameof(point));
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z))
            throw new ArgumentException("point must be finite", nameof(point));
        try
        {
            MapExactXz xz = point.ExactWorldXz();
            MapExactValue y = point.ExactY();
            MapSurfaceScope scope = _view.Scope;
            if (point.Local.X < scope.LocalMin.X || point.Local.X > scope.LocalMax.X ||
                point.Local.Z < scope.LocalMin.Y || point.Local.Z > scope.LocalMax.Y ||
                point.Local.Y < scope.MinY || point.Local.Y > scope.MaxY)
                return Failure(MapMembershipStatus.MissingGeometry, "outside acquired scope");
            if (_view.Status == MapAcquireStatus.CapacityExceeded) return Failure(MapMembershipStatus.CapacityExceeded, _view.Detail);
            if (_view.Status == MapAcquireStatus.NotRepresentable) return Failure(MapMembershipStatus.NotRepresentable, _view.Detail);
            MapQueryLimits budgets = scope.Limits;
            var limits = new MapRefinementLimits(MaxContextPatches: budgets.MaxCandidatePatches, MaxContextFaces: budgets.MaxInspectedFaces);
            var bounds = supplied is null ? new MapSpacePointBounds(_view, limits, work) : new(supplied, limits);
            MapTopologyRecord[] records = MapSpaceGeometry.Records(_view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            var candidates = new List<Candidate>();
            var demands = new List<MapBoundFaceDemand>();
            var portalChains = new Dictionary<MapRecordRef, (MapBoundaryChain Chain, MapCellDemand[] Cells)>();
            foreach (MapSpaceFootprint footprint in records.OfType<MapSpaceFootprint>())
            {
                if (MapSpaceGeometry.FootprintCell(_view, footprint, xz) is null) continue;
                _ = MapSpaceGeometry.Record<MapSpaceDoc>(_view, footprint.Space);
                MapSpacePointBounds.Bound lower = bounds.Plan(footprint, footprint.Lower, xz);
                if (lower.Status != MapMembershipStatus.Resolved) return Failure(lower.Status, $"footprint '{footprint.Id}': {lower.Detail}");
                MapSpacePointBounds.Bound upper = bounds.Plan(footprint, footprint.Upper, xz);
                if (upper.Status != MapMembershipStatus.Resolved) return Failure(upper.Status, $"footprint '{footprint.Id}': {upper.Detail}");
                candidates.Add(new(footprint, lower, upper));
                Add(lower);
                Add(upper);
            }
            MapCavePortal[] incident = records.OfType<MapCavePortal>().Where(p => OnPortal(p, xz)).ToArray();
            foreach (MapCavePortal portal in incident)
            {
                DemandChain(portal.BandBottom);
                if (portal.BandTop is { } top) DemandChain(top);
            }
            foreach (MapSurfaceRef surface in _view.Surfaces.Where(s => s.IndoorSpan is not null))
            {
                var (x, z) = MapSpaceGeometry.Cell(surface.Frame, xz);
                MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) || patch is null) continue;
                int slot = MapLowerCellClassifier.SlotCell(x, z), lx = slot % 64 - patch.CellMinX, lz = slot / 64 - patch.CellMinZ;
                if (lx >= 0 && lx < patch.Width && lz >= 0 && lz < patch.Depth &&
                    (patch.Cells[lz * patch.Width + lx].Flags & MapCellFlags.Indoor) != 0)
                    demands.Add(new(bounds.SourceCell(key, xz), null));
            }
            MapBoundFaceContext? context = supplied ?? MapBoundFaceContext.PrepareBounds(_view, demands,
                budgets.MaxCandidatePatches, budgets.MaxInspectedFaces, work, out _, bounds.Preparation);
            if (context is null) return Failure(MapMembershipStatus.CapacityExceeded, bounds.Preparation.Refusal ?? "context faces");
            foreach (MapCavePortal portal in incident)
            {
                MapExactValue bottom = PortalHeight(portal.BandBottom);
                MapExactValue? top = portal.BandTop is { } reference ? PortalHeight(reference) : null;
                if (y.CompareTo(bottom) >= 0 && (top is null || y.CompareTo(top.Value) < 0))
                    return Resolve(MapSpaceGeometry.Record<MapSpaceDoc>(_view, portal.ToSpace), null, context);
            }
            var included = new Dictionary<string, (MapSpaceDoc Space, MapLegacyCellTag? Compatibility)>(StringComparer.Ordinal);
            foreach (Candidate candidate in candidates)
            {
                MapExactValue? lower = MapSpacePointBounds.Height(context, candidate.Lower, xz);
                MapExactValue? upper = MapSpacePointBounds.Height(context, candidate.Upper, xz);
                if (lower is null || (candidate.Footprint.Upper.Kind != MapBoundKind.OpenTop && upper is null))
                    return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: footprint '{candidate.Footprint.Id}'");
                if (y.CompareTo(lower.Value) < 0 || (upper is { } high && y.CompareTo(high) >= 0)) continue;
                MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, candidate.Footprint.Space);
                string owner = MapSpaceGeometry.Owner(_view, space);
                MapSpaceDoc target = space.AliasOf is { } alias ? MapSpaceGeometry.Record<MapSpaceDoc>(_view, alias) : space;
                included.TryAdd(owner, (target, candidate.Lower.Compatibility));
            }
            if (included.Count > 1) return Failure(MapMembershipStatus.Ambiguous, "ambiguous occupied spaces: " + string.Join(", ", included.Keys.Order(StringComparer.Ordinal)));
            if (included.Count == 1)
            {
                var selected = included.Values.Single();
                return Resolve(selected.Space, selected.Compatibility, context);
            }
            return _view.Status == MapAcquireStatus.Incomplete
                ? Failure(MapMembershipStatus.MissingGeometry, Unavailable()) : Failure(MapMembershipStatus.Outside, null);

            void Add(MapSpacePointBounds.Bound bound)
            {
                if (bound.Cell is { } cell && bound.Compatibility is null) demands.Add(new(cell, bound.Opening));
            }
            void DemandChain(MapRecordRef reference)
            {
                if (portalChains.ContainsKey(reference)) return;
                MapBoundaryChain chain = PointChain(MapSpaceGeometry.Record<MapBoundaryChain>(_view, reference), xz);
                MapCellDemand[] cells = bounds.SourceCells(chain).ToArray();
                portalChains.Add(reference, (chain, cells));
                demands.AddRange(cells.Select(cell => new MapBoundFaceDemand(cell, null)));
            }
            MapExactValue PortalHeight(MapRecordRef reference)
            {
                var (source, cells) = portalChains[reference];
                // A supplied context must also have prepared every source cell for this selected segment.
                foreach (MapCellDemand cell in cells) _ = context.Faces(cell);
                MapChainResolution chain = MapBoundaryGeometry.ResolveChain(source, _view,
                    key => new MapBoundarySurface(context.ValidatedPatch(key), MapSlotCellMask.Of(cells.Select(cell => cell.SlotCell))));
                if (chain.Detail?.Contains("not representable", StringComparison.Ordinal) == true) throw new MapExactOverflowException();
                if (chain.Status != MapResolveStatus.Resolved) throw new MapDocumentException(chain.Detail ?? "missing geometry: portal band");
                return MapSpaceGeometry.ChainHeight(chain.Points, xz) ?? throw new MapDocumentException("invalid portal vertex sequence");
            }
            MapMembershipResult Resolve(MapSpaceDoc space, MapLegacyCellTag? compatibility, MapBoundFaceContext prepared)
            {
                if (space.AliasOf is { } alias) space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, alias);
                IReadOnlyList<MapSpaceDoc> ancestry = MapSpaceGeometry.Ancestors(_view, space);
                var keys = ancestry.Select(s => s.Id).Concat(ancestry.SelectMany(s => s.DomainTags)).ToList();
                foreach (MapSurfaceRef surface in _view.Surfaces.OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    if (surface.IndoorSpan is not { } span || !ancestry.Any(s => s.Id == span.ParentSpace.Id)) continue;
                    _ = MapSpaceGeometry.Record<MapSpaceDoc>(_view, span.ParentSpace);
                    var (x, z) = MapSpaceGeometry.Cell(surface.Frame, xz);
                    MapCellDemand cell = new(MapPatchKey.ForCell(surface.Id, x, z), MapLowerCellClassifier.SlotCell(x, z));
                    if (!_view.TryAcquiredPatch(cell.Patch, out MapSurfacePatch? patch, out MapPatchStatus status))
                        return Failure(MapMembershipStatus.MissingGeometry, $"indoor span '{span.Id}' patch {cell.Patch}: {status}");
                    if (patch is null) continue;
                    int lx = cell.SlotCell % 64 - patch.CellMinX, lz = cell.SlotCell / 64 - patch.CellMinZ;
                    if (lx < 0 || lx >= patch.Width || lz < 0 || lz >= patch.Depth ||
                        (patch.Cells[lz * patch.Width + lx].Flags & MapCellFlags.Indoor) == 0) continue;
                    MapExactValue? floor = MapSpaceGeometry.Height(prepared.Faces(cell), xz);
                    if (floor is null) return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: indoor span '{span.Id}'");
                    MapExactValue lo = floor.Value.Add(surface.Frame.Metres(new(span.LowerOffsetUnits, 1)));
                    MapExactValue hi = floor.Value.Add(surface.Frame.Metres(new(span.UpperOffsetUnits, 1)));
                    if (y.CompareTo(lo) >= 0 && y.CompareTo(hi) < 0) { keys.Add(span.Id); keys.AddRange(span.DomainTags); }
                }
                return new(MapMembershipStatus.Resolved, point, space.Id, keys.Distinct(StringComparer.Ordinal), compatibility, null, _view.ReadWitness);
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Failure(MapMembershipStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return Failure(MapCommonRefinement.NotRepresentable(error) ? MapMembershipStatus.NotRepresentable :
                error.Message.Contains("invalid", StringComparison.Ordinal) || error.Message.Contains("legacy recipe", StringComparison.Ordinal)
                    ? MapMembershipStatus.Invalid : MapMembershipStatus.MissingGeometry, error.Message);
        }

        string Unavailable() => "missing geometry: " + string.Join(", ", _view.Witness.Unavailable.Select(u => $"{u.What} {u.Key} ({u.Status})"));
        MapMembershipResult Failure(MapMembershipStatus status, string? detail) =>
            new(status, point, null, Array.Empty<string>(), null, detail, _view.ReadWitness);
    }

    bool OnPortal(MapCavePortal portal, MapExactXz point)
    {
        for (int i = 0; i + 1 < portal.Interval.Count; i++)
        {
            MapLatticeVertex a = portal.Interval[i], b = portal.Interval[i + 1];
            if (MapSpaceGeometry.OnSegment(point, MapSpaceGeometry.Surface(_view, a.SurfaceId).Frame.WorldXz(a.Address),
                MapSpaceGeometry.Surface(_view, b.SurfaceId).Frame.WorldXz(b.Address))) return true;
        }
        return false;
    }

    MapBoundaryChain PointChain(MapBoundaryChain chain, MapExactXz point)
    {
        for (int i = 0; i + 1 < chain.Vertices.Count; i++)
        {
            MapLatticeVertex first = chain.Vertices[i].Vertex, last = chain.Vertices[i + 1].Vertex;
            MapExactXz a = MapSpaceGeometry.Surface(_view, first.SurfaceId).Frame.WorldXz(first.Address);
            MapExactXz b = MapSpaceGeometry.Surface(_view, last.SurfaceId).Frame.WorldXz(last.Address);
            if (!MapSpaceGeometry.OnSegment(point, a, b) || (point == b && i + 2 < chain.Vertices.Count)) continue;
            return chain with { Vertices = Array.AsReadOnly(new[] { chain.Vertices[i], chain.Vertices[i + 1] }) };
        }
        throw new MapDocumentException("invalid portal vertex sequence");
    }
}
