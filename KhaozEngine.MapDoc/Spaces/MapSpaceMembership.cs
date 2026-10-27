using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
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
    public MapExactPoint ExactPoint { get; }
    public string? SpaceId { get; }
    public IReadOnlyList<string> ParentSpaceIds { get; }
    public MapOccupiedFragment? Fragment { get; }
    public IReadOnlyList<string> DomainKeys { get; }
    public MapLegacyCellTag? LowerCompatibility { get; }
    public string? Detail { get; }
    public MapReadWitness Witness { get; }

    internal MapMembershipResult(MapMembershipStatus status, MapFramePoint point, string? spaceId,
        IEnumerable<string> domainKeys, MapLegacyCellTag? lowerCompatibility, string? detail, MapReadWitness witness, MapExactPoint? exactPoint = null, IEnumerable<string>? parentSpaceIds = null, MapOccupiedFragment? fragment = null)
    {
        Status = status;
        Point = point;
        ExactPoint = exactPoint ?? ExactOrDefault(point);
        SpaceId = spaceId;
        ParentSpaceIds = Array.AsReadOnly((parentSpaceIds ?? Array.Empty<string>()).ToArray());
        Fragment = fragment;
        DomainKeys = Array.AsReadOnly(domainKeys.ToArray());
        LowerCompatibility = lowerCompatibility;
        Detail = detail;
        Witness = witness;
    }
    static MapExactPoint ExactOrDefault(MapFramePoint point)
    {
        try { var xz = point.ExactWorldXz(); return new(xz.X, point.ExactY(), xz.Z); }
        catch (MapExactOverflowException) { return default; }
    }

}

/// <summary>Resolves membership using only acquired records and one point-cell compilation context.</summary>
public sealed class MapSpaceMembership
{
    readonly MapScopedSurfaces _view;
    readonly Func<MapTopologyRecord[]>? _preparedRecords;
    public MapSpaceMembership(MapScopedSurfaces scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        _view = scoped;
    }

    internal MapSpaceMembership(MapScopedSurfaces scoped, Func<MapTopologyRecord[]> records) : this(scoped) => _preparedRecords = records;

    internal MapMembershipObservation Query(MapFramePoint point, MapSpaceMembershipScratch scratch) => scratch.Query(_view, point);

    public MapMembershipResult Query(MapFramePoint point) => QueryCore(point, null, null, null, out _, null, false);
    internal MapMembershipResult Query(MapFramePoint point, MapBoundFaceWork work) => QueryCore(point, work, null, null, out _, null, false);
    internal MapMembershipResult Query(MapFramePoint point, IEnumerable<MapBoundFaceDemand> demands,
        MapBoundFaceWork? work, out MapBoundFaceContext? prepared) => QueryCore(point, work, null, demands, out prepared, null, false);
    internal MapMembershipResult Query(MapFramePoint point, MapBoundFaceContext context)
    {
        context.RequireView(_view);
        return QueryCore(point, null, context, null, out _, null, false);
    }

    public MapMembershipResult QueryExact(MapExactPoint world) => QueryCore(MapEffectiveGeometry.Presentation(_view, world), null, null, null, out _, null, false, world);
    internal MapMembershipResult QueryExact(MapExactPoint world, IEnumerable<MapBoundFaceDemand> demands,
        MapBoundFaceWork? work, out MapBoundFaceContext? prepared) =>
        QueryCore(MapEffectiveGeometry.Presentation(_view, world), work, null, demands, out prepared, null, false, world);
    internal MapMembershipResult QueryExact(MapExactPoint world, out MapSpaceBoundsPart? selected)
    {
        var parts = new List<MapSpaceBoundsPart>(1);
        var result = QueryCore(MapEffectiveGeometry.Presentation(_view, world), null, null, null, out _, parts, false, world);
        selected = result.Status == MapMembershipStatus.Resolved ? parts.Single() : null;
        return result;
    }

    /// <summary>Resolves <paramref name="point"/> exactly as <see cref="Query(MapFramePoint)"/> does and reports the
    /// exact bounds of the candidate or portal band it selected.</summary>
    internal MapMembershipResult Query(MapFramePoint point, out MapSpaceBoundsPart? selected)
    {
        var parts = new List<MapSpaceBoundsPart>(1);
        MapMembershipResult result = QueryCore(point, null, null, null, out _, parts, false);
        selected = result.Status == MapMembershipStatus.Resolved ? parts.Single() : null;
        return result;
    }

    /// <summary>Evaluates every candidate footprint and incident portal band at the point's XZ without the Y filter.
    /// A failure any height of the column would meet is returned with no parts, an acquisition bounded in height is
    /// <see cref="MapMembershipStatus.MissingGeometry"/>, and an empty column is
    /// <see cref="MapMembershipStatus.Outside"/>.</summary>
    internal MapMembershipResult Column(MapFramePoint point, out IReadOnlyList<MapSpaceBoundsPart> parts)
    {
        var found = new List<MapSpaceBoundsPart>();
        MapMembershipResult result = QueryCore(point, null, null, null, out _, found, true);
        parts = result.Status == MapMembershipStatus.Resolved ? found.AsReadOnly() : Array.Empty<MapSpaceBoundsPart>();
        return result;
    }

    /// <summary>Point membership over a supplied context, as the point's own query would answer it: the point demands the
    /// extra cells, and refuses CapacityExceeded when its own demand set exceeds its face allowance. Reports a prepared
    /// context only after the point's preparation succeeds, including when the context is supplied.</summary>
    internal MapMembershipResult QueryExact(MapExactPoint world, MapBoundFaceContext context, IEnumerable<MapBoundFaceDemand> demands,
        out MapBoundFaceContext? prepared)
    {
        context.RequireView(_view);
        return QueryCore(MapEffectiveGeometry.Presentation(_view, world), null, context, demands, out prepared, null, false, world, pointBudget: true);
    }
    internal MapMembershipResult Query(MapFramePoint point, MapBoundFaceContext context, IEnumerable<MapBoundFaceDemand> demands,
        out MapBoundFaceContext? prepared)
    {
        context.RequireView(_view);
        return QueryCore(point, null, context, demands, out prepared, null, false, pointBudget: true);
    }

    internal MapMembershipResult QueryExact(MapExactPoint world, MapBoundFaceContext context)
    {
        context.RequireView(_view);
        return QueryCore(MapEffectiveGeometry.Presentation(_view, world), null, context, null, out _, null, false, world);
    }
    internal MapMembershipResult Column(MapFramePoint point, MapExactPoint? world, MapBoundFaceContext context,
        out IReadOnlyList<MapSpaceBoundsPart> parts)
    {
        context.RequireView(_view);
        var found = new List<MapSpaceBoundsPart>();
        var result = QueryCore(point, null, context, null, out _, found, true, world);
        parts = result.Status == MapMembershipStatus.Resolved ? found.AsReadOnly() : Array.Empty<MapSpaceBoundsPart>();
        return result;
    }

    sealed record Candidate(MapSpaceFootprint Footprint, MapSpacePointBounds.Bound Lower, MapSpacePointBounds.Bound Upper);

    MapMembershipResult QueryCore(MapFramePoint point, MapBoundFaceWork? work, MapBoundFaceContext? supplied,
        IEnumerable<MapBoundFaceDemand>? additional, out MapBoundFaceContext? prepared, List<MapSpaceBoundsPart>? parts, bool column, MapExactPoint? exactWorld = null,
        bool pointBudget = false)
    {
        using var demandScope = DemandDiagnostics.Enter(this, "MapSpaceMembership.Query");
        prepared = null;
        if (point.Frame != _view.Scope.Frame) throw new ArgumentException("point frame differs from acquired frame", nameof(point));
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z))
            throw new ArgumentException("point must be finite", nameof(point));
        try
        {
            MapExactXz xz = exactWorld is { } exact ? new(exact.X, exact.Z) : point.ExactWorldXz();
            // A column reads every height, so the point's own Y never enters it.
            MapExactValue y = column ? default : exactWorld?.Y ?? point.ExactY();
            MapSurfaceScope scope = _view.Scope;
            if (exactWorld is { } suppliedPoint ? !MapEffectiveGeometry.InScope(_view, suppliedPoint) : point.Local.X < scope.LocalMin.X || point.Local.X > scope.LocalMax.X ||
                point.Local.Z < scope.LocalMin.Y || point.Local.Z > scope.LocalMax.Y ||
                (!column && (point.Local.Y < scope.MinY || point.Local.Y > scope.MaxY)))
                return Failure(MapMembershipStatus.MissingGeometry, "outside acquired scope");
            // A column covers every height, so any height bound on the acquisition leaves part of it unread.
            if (column && (scope.MinY is not null || scope.MaxY is not null))
                return Failure(MapMembershipStatus.MissingGeometry, "column outside acquired vertical scope");
            if (_view.Status == MapAcquireStatus.CapacityExceeded) return Failure(MapMembershipStatus.CapacityExceeded, _view.Detail);
            if (_view.Status == MapAcquireStatus.NotRepresentable) return Failure(MapMembershipStatus.NotRepresentable, _view.Detail);
            if (_view.EffectiveContext is not null && _view.Status == MapAcquireStatus.Incomplete) return Failure(MapMembershipStatus.MissingGeometry, Unavailable());
            MapQueryLimits budgets = scope.Limits;
            var limits = budgets.Refinement is { } declared ? declared with
            { MaxContextPatches = Math.Min(declared.MaxContextPatches, budgets.MaxCandidatePatches), MaxContextFaces = Math.Min(declared.MaxContextFaces, budgets.MaxInspectedFaces) }
                : new MapRefinementLimits(MaxContextPatches: budgets.MaxCandidatePatches, MaxContextFaces: budgets.MaxInspectedFaces);
            var bounds = supplied is null ? new MapSpacePointBounds(_view, limits, work) : new(supplied, limits);
            bounds.Preparation.PatchQueryLimits = budgets;
            MapTopologyRecord[] records = _preparedRecords?.Invoke() ?? MapSpaceGeometry.Records(_view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            var prisms = records.OfType<MapPrismDomain>().ToArray();
            var roomIds = records.OfType<MapSpaceDoc>().Where(s => s.Kind == MapSpaceKind.Room).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            if (records.OfType<MapSpaceFootprint>().Any(f => roomIds.Contains(f.Space.Id)))
                return Failure(MapMembershipStatus.MissingGeometry, "room occupancy requires its prism, not a second lattice domain");
            foreach (var room in records.OfType<MapSpaceDoc>().Where(s => s.Kind == MapSpaceKind.Room && s.AliasOf is null))
                if (prisms.Count(p => p.Space.Id == room.Id) != 1) return Failure(MapMembershipStatus.MissingGeometry, "room requires one acquired prism");
            long prismVertices = prisms.Sum(MapPrismGeometry.Cost);
            if (DemandDiagnostics.Check(this, "MapSpaceMembership.MaxRecordReads.1", "MaxRecordReads", 0, prisms.Length, budgets.MaxRecordReads, prisms.Length > budgets.MaxRecordReads, CapacityDemandLifetime.Cumulative) || DemandDiagnostics.Check(this, "MapSpaceMembership.MaxInspectedFaces.1", "MaxInspectedFaces", 0, prismVertices, budgets.MaxInspectedFaces, prismVertices > budgets.MaxInspectedFaces, CapacityDemandLifetime.Cumulative))
                return Failure(MapMembershipStatus.CapacityExceeded, "prism records or vertices");
            int remainingFaces = budgets.MaxInspectedFaces - (int)prismVertices;
            bool nested = prisms.Length != 0;
            if (nested && _view.Status == MapAcquireStatus.Incomplete) return Failure(MapMembershipStatus.MissingGeometry, Unavailable());
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
                if (prisms.Any(p => p.Space.Id == portal.FromSpace.Id || p.Space.Id == portal.ToSpace.Id)) continue;
                DemandChain(portal.BandBottom);
                if (portal.BandTop is { } top) DemandChain(top);
            }
            foreach (MapSurfaceRef surface in _view.Surfaces.Where(s => s.IndoorSpan is not null))
            {
                foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(_view, surface.Id, xz))
                {
                    MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                    if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) || patch is null) continue;
                    int slot = MapLowerCellClassifier.SlotCell(x, z), lx = slot % 64 - patch.CellMinX, lz = slot / 64 - patch.CellMinZ;
                    if (lx >= 0 && lx < patch.Width && lz >= 0 && lz < patch.Depth &&
                        (patch.Cells[lz * patch.Width + lx].Flags & MapCellFlags.Indoor) != 0)
                        demands.Add(new(new(key, slot), null));
                }
            }
            if (additional is not null) demands.AddRange(additional);
            // A supplied context may hold more than this point demands. The point still keeps its own face allowance.
            if (supplied is not null && pointBudget && !WithinPointFaces(demands, remainingFaces, prismVertices, budgets.MaxInspectedFaces))
                return Failure(MapMembershipStatus.CapacityExceeded, "context faces");
            MapBoundFaceContext? context = supplied ?? MapBoundFaceContext.PrepareBounds(_view, demands,
                budgets.MaxCandidatePatches, remainingFaces, work, out _, bounds.Preparation,
                faceOrigin: new("MaxInspectedFaces", prismVertices, budgets.MaxInspectedFaces, budgets));
            prepared = context;
            if (context is null) return Failure(MapMembershipStatus.CapacityExceeded, bounds.Preparation.Refusal ?? "context faces");
            if (!column)
            {
                MapMembershipStatus pointFailure = MapMembershipStatus.Resolved;
                string? pointDetail = null;
                var input = new MapMembershipColdInput(_view, xz, prisms, candidates.Count, incident.Length,
                    PointCandidate, PointPortal, () => pointFailure);
                var scratch = new MapMembershipPointScratch(Math.Max(1, candidates.Count + prisms.Length), prisms.Length, canGrow: true);
                MapMembershipPointKernel.Query(input, y, scratch);
                if (pointFailure != MapMembershipStatus.Resolved) return Failure(pointFailure, pointDetail);
                if (scratch.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside or MapMembershipStatus.Ambiguous))
                    return Failure(scratch.Status, scratch.Detail);
                if (scratch.Status == MapMembershipStatus.Ambiguous)
                    return Failure(MapMembershipStatus.Ambiguous, "ambiguous occupied spaces: " + string.Join(", ",
                        scratch.Included.Take(scratch.IncludedCount).Where((p, i) => !scratch.Removed[i]).Select(p => p.Owner).Order(StringComparer.Ordinal)));
                if (scratch.Selected is { } picked) return Resolve(picked.Space, picked.Compatibility, context, picked.Cold);
                return _view.Status == MapAcquireStatus.Incomplete
                    ? Failure(MapMembershipStatus.MissingGeometry, Unavailable()) : Failure(MapMembershipStatus.Outside, null);

                MapMembershipPart? PointPortal(int index)
                {
                    MapCavePortal portal = incident[index];
                    if (prisms.Any(p => p.Space.Id == portal.FromSpace.Id || p.Space.Id == portal.ToSpace.Id)) return null;
                    MapExactValue bottom = PortalHeight(portal.BandBottom);
                    MapExactValue? top = portal.BandTop is { } reference ? PortalHeight(reference) : null;
                    if (y.CompareTo(bottom) < 0 || top is { } ceiling && y.CompareTo(ceiling) >= 0) return null;
                    var to = MapSpaceGeometry.Record<MapSpaceDoc>(_view, portal.ToSpace);
                    var band = new MapSpaceBoundsPart(Target(to).Id, Target(to).Id, bottom, top,
                        portal.BandBottom.Id, portal.BandTop?.Id ?? MapSpaceInterval.OpenTop, true);
                    return new(to, band.Owner, bottom, top, band);
                }
                MapMembershipPart? PointCandidate(int index)
                {
                    Candidate candidate = candidates[index];
                    MapExactValue? lower = MapSpacePointBounds.Height(context, candidate.Lower, xz);
                    MapExactValue? upper = MapSpacePointBounds.Height(context, candidate.Upper, xz);
                    if (lower is null || candidate.Footprint.Upper.Kind != MapBoundKind.OpenTop && upper is null)
                    {
                        pointFailure = MapMembershipStatus.MissingGeometry;
                        pointDetail = $"missing bound: footprint '{candidate.Footprint.Id}'";
                        return null;
                    }
                    if (nested ? upper is { } end && lower.Value.CompareTo(end) >= 0
                        : y.CompareTo(lower.Value) < 0 || upper is { } high && y.CompareTo(high) >= 0) return null;
                    var space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, candidate.Footprint.Space);
                    var part = new MapSpaceBoundsPart(Target(Target(space)).Id, MapSpaceGeometry.Owner(_view, space), lower.Value, upper,
                        BoundaryId(candidate.Footprint.Lower), BoundaryId(candidate.Footprint.Upper), false)
                    { LowerFaces = Faces(candidate.Lower), UpperFaces = Faces(candidate.Upper) };
                    if (nested) part = MapOccupiedPartitions.Identify(_view, part);
                    return new(Target(space), part.Owner, lower.Value, upper, part, Compatibility: nested ? null : candidate.Lower.Compatibility);
                }
            }
            var targets = new Dictionary<string, MapSpaceDoc>(StringComparer.Ordinal);
            foreach (MapCavePortal portal in incident)
            {
                if (prisms.Any(p => p.Space.Id == portal.FromSpace.Id || p.Space.Id == portal.ToSpace.Id)) continue;
                MapExactValue bottom = PortalHeight(portal.BandBottom);
                MapExactValue? top = portal.BandTop is { } reference ? PortalHeight(reference) : null;
                MapSpaceDoc to = MapSpaceGeometry.Record<MapSpaceDoc>(_view, portal.ToSpace);
                // Only the column groups owners. A point resolves the band's space directly, as it always has.
                var band = new MapSpaceBoundsPart(Target(to).Id, MapSpaceGeometry.Owner(_view, to),
                    bottom, top, portal.BandBottom.Id, portal.BandTop?.Id ?? MapSpaceInterval.OpenTop, true);
                Collect(to, band);
            }
            var canonical = new List<MapSpaceBoundsPart>();
            foreach (Candidate candidate in candidates)
            {
                MapExactValue? lower = MapSpacePointBounds.Height(context, candidate.Lower, xz);
                MapExactValue? upper = MapSpacePointBounds.Height(context, candidate.Upper, xz);
                if (lower is null || (candidate.Footprint.Upper.Kind != MapBoundKind.OpenTop && upper is null))
                    return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: footprint '{candidate.Footprint.Id}'");
                // An empty candidate holds no height, so point membership can never select it.
                if (upper is { } end && lower.Value.CompareTo(end) >= 0) continue;
                MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, candidate.Footprint.Space);
                // Resolve maps the selected target's alias once more, so the part names the space it reports.
                var part = new MapSpaceBoundsPart(Target(Target(space)).Id, MapSpaceGeometry.Owner(_view, space), lower.Value, upper,
                    BoundaryId(candidate.Footprint.Lower), BoundaryId(candidate.Footprint.Upper), false)
                { LowerFaces = Faces(candidate.Lower), UpperFaces = Faces(candidate.Upper) };
                if (nested) canonical.Add(part);
                else Collect(Target(space), part);
            }
            if (nested)
            {
                foreach (var occupied in MapOccupiedPartitions.At(_view, xz, canonical, prisms))
                {
                    var space = records.OfType<MapSpaceDoc>().First(s => s.Id == occupied.SpaceId);
                    Collect(space, occupied);
                }
            }
            // Every gap in the column is a height where an unloaded record could own the point.
            if (_view.Status == MapAcquireStatus.Incomplete) return Failure(MapMembershipStatus.MissingGeometry, Unavailable());
            foreach (MapSpaceDoc target in targets.Values)
                if (Resolve(target, null, context, null) is { Status: not MapMembershipStatus.Resolved } failure)
                {
                    parts!.Clear();
                    return failure;
                }
            return parts!.Count == 0 ? Failure(MapMembershipStatus.Outside, null)
                : new(MapMembershipStatus.Resolved, point, null, Array.Empty<string>(), null, null, _view.ReadWitness);

            MapSpaceDoc Target(MapSpaceDoc space) => space.AliasOf is { } alias ? MapSpaceGeometry.Record<MapSpaceDoc>(_view, alias) : space;
            // Records the space exactly as point membership would pass it to Resolve, so the column meets its failures.
            void Collect(MapSpaceDoc resolved, MapSpaceBoundsPart part)
            {
                targets.TryAdd(resolved.Id, resolved);
                parts!.Add(part);
            }

            IReadOnlyList<MapBoundFace> Faces(MapSpacePointBounds.Bound bound) => bound.Cell is not { } cell || bound.Compatibility is not null
                ? Array.Empty<MapBoundFace>() : bound.Opening is { } opening ? context.Opening(opening, cell) : context.Faces(cell);
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
            MapMembershipResult Resolve(MapSpaceDoc space, MapLegacyCellTag? compatibility, MapBoundFaceContext prepared,
                MapSpaceBoundsPart? selected)
            {
                if (space.AliasOf is { } alias) space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, alias);
                IReadOnlyList<MapSpaceDoc> ancestry = MapSpaceGeometry.Ancestors(_view, space);
                var keys = ancestry.Select(s => s.Id).Concat(ancestry.SelectMany(s => s.DomainTags)).ToList();
                foreach (MapSurfaceRef surface in _view.Surfaces.OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    if (surface.IndoorSpan is not { } span || !ancestry.Any(s => s.Id == span.ParentSpace.Id)) continue;
                    _ = MapSpaceGeometry.Record<MapSpaceDoc>(_view, span.ParentSpace);
                    foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(_view, surface.Id, xz))
                    {
                        MapCellDemand cell = new(MapPatchKey.ForCell(surface.Id, x, z), MapLowerCellClassifier.SlotCell(x, z));
                        if (!_view.TryAcquiredPatch(cell.Patch, out MapSurfacePatch? patch, out MapPatchStatus status))
                            return Failure(MapMembershipStatus.MissingGeometry, $"indoor span '{span.Id}' patch {cell.Patch}: {status}");
                        if (patch is null) continue;
                        int lx = cell.SlotCell % 64 - patch.CellMinX, lz = cell.SlotCell / 64 - patch.CellMinZ;
                        if (lx < 0 || lx >= patch.Width || lz < 0 || lz >= patch.Depth ||
                            (patch.Cells[lz * patch.Width + lx].Flags & MapCellFlags.Indoor) == 0) continue;
                        MapExactValue? lo = MapEffectiveGeometry.SpanHeight(prepared, cell, xz, span.LowerOffsetUnits);
                        MapExactValue? hi = MapEffectiveGeometry.SpanHeight(prepared, cell, xz, span.UpperOffsetUnits);
                        if (lo is null || hi is null) return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: indoor span '{span.Id}'");
                        if (y.CompareTo(lo.Value) >= 0 && y.CompareTo(hi.Value) < 0) { keys.Add(span.Id); keys.AddRange(span.DomainTags); }
                    }
                }
                if (selected is not null) { selected = MapOccupiedPartitions.Identify(_view, selected); parts?.Add(selected); }
                return new(MapMembershipStatus.Resolved, point, space.Id, keys.Distinct(StringComparer.Ordinal), compatibility, null, _view.ReadWitness, exactWorld, ancestry.Skip(1).Select(s => s.Id), selected?.Fragment);
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Failure(MapMembershipStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return Failure(error.Message.Contains("capacity exceeded", StringComparison.Ordinal) || error.Message.Contains("domain work exceeds", StringComparison.Ordinal) ? MapMembershipStatus.CapacityExceeded :
                MapCommonRefinement.NotRepresentable(error) || error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase) ? MapMembershipStatus.NotRepresentable :
                error.Message.Contains("invalid", StringComparison.Ordinal) || error.Message.Contains("legacy recipe", StringComparison.Ordinal)
                    ? MapMembershipStatus.Invalid : MapMembershipStatus.MissingGeometry, error.Message);
        }

        string Unavailable() => "missing geometry: " + string.Join(", ", _view.Witness.Unavailable.Select(u => $"{u.What} {u.Key} ({u.Status})"));
        MapMembershipResult Failure(MapMembershipStatus status, string? detail)
        {
            parts?.Clear();
            return new(status, point, null, Array.Empty<string>(), null, detail, _view.ReadWitness, exactWorld);
        }
    }

    // The face count a point's own context would charge: each distinct demanded cell's physical faces and each distinct
    // opening cell's plane faces, as MapBoundFaceContext.PrepareBounds counts them, against the point's allowance.
    bool WithinPointFaces(List<MapBoundFaceDemand> demands, long maxFaces, long consumed, long allowance)
    {
        var cells = new HashSet<MapCellDemand>();
        var openings = new HashSet<(MapRecordRef, int)>();
        long count = 0;
        foreach (MapBoundFaceDemand demand in demands)
        {
            MapCellDemand cell = demand.Cell;
            bool freshCell = cells.Add(cell);
            bool freshOpening = demand.Opening is { } opening && openings.Add((opening, cell.SlotCell));
            if (!freshCell && !freshOpening) continue;
            MapSurfaceRef surface = MapSpaceGeometry.Surface(_view, cell.Patch.SurfaceId);
            if (!_view.TryAcquiredPatch(cell.Patch, out MapSurfacePatch? patch, out _) || patch is null)
                throw new MapDocumentException($"context demand patch {cell.Patch}: unavailable");
            if (freshCell) count = checked(count + MapSurfaceCompiler.CountFaces(surface, patch, cell.SlotCell));
            if (freshOpening) count = checked(count + MapBoundFaceContext.OpeningFaceCount(patch, cell.SlotCell));
            if (DemandDiagnostics.Check(this, "MapSpaceMembership.MaxInspectedFaces.2", "MaxInspectedFaces", consumed, count, allowance, count > maxFaces, CapacityDemandLifetime.Cumulative)) return false;
        }
        return true;
    }

    static string BoundaryId(MapBoundRef bound) => bound.SurfaceId ?? bound.Opening?.Id ?? MapSpaceInterval.OpenTop;

    internal bool OnPortal(MapCavePortal portal, MapExactXz point)
    {
        for (int i = 0; i + 1 < portal.Interval.Count; i++)
        {
            MapLatticeVertex a = portal.Interval[i], b = portal.Interval[i + 1];
            if (MapSpaceGeometry.OnSegment(point, MapEffectiveGeometry.Vertex(_view, a),
                MapEffectiveGeometry.Vertex(_view, b))) return true;
        }
        return false;
    }

    MapBoundaryChain PointChain(MapBoundaryChain chain, MapExactXz point)
    {
        for (int i = 0; i + 1 < chain.Vertices.Count; i++)
        {
            MapLatticeVertex first = chain.Vertices[i].Vertex, last = chain.Vertices[i + 1].Vertex;
            MapExactXz a = MapEffectiveGeometry.Vertex(_view, first);
            MapExactXz b = MapEffectiveGeometry.Vertex(_view, last);
            if (!MapSpaceGeometry.OnSegment(point, a, b) || (point == b && i + 2 < chain.Vertices.Count)) continue;
            return chain with { Vertices = Array.AsReadOnly(new[] { chain.Vertices[i], chain.Vertices[i + 1] }) };
        }
        throw new MapDocumentException("invalid portal vertex sequence");
    }
}
