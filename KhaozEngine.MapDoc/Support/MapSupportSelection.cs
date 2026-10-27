using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Support;

/// <summary>The one support selection kernel, shared by the released point query and prepared support. It consumes the
/// full request: point, SurfaceId and SpaceId filters, CurrentSupport, both search extents and the slope limit, plus
/// named-owner mode and an optional exact world point. Validation, refusal order and point-level budgets are the
/// released query's.</summary>
internal static class MapSupportSelection
{
    internal sealed record Candidate(MapCompiledFace Face, MapExactValue Height, string? SpaceId, MapRecordRef? Via);
    internal sealed record Outcome(MapSupportResult Result, IReadOnlyList<Candidate> Candidates);
    sealed record Intersection(MapCompiledFace Face, MapExactValue Height);

    internal static MapSupportResult Select(MapPreparedSupport prepared, MapSupportRequest request, bool namedSurfaceOnly,
        MapExactPoint? exactWorld = null) => Compute(prepared, request, null, namedSurfaceOnly, exactWorld).Result;

    /// <summary>The request a support binding makes at an exact world XZ, or null when the binding lacks its kind's
    /// members. The query point takes the binding's reference height, zero for a Surface binding.</summary>
    internal static (MapSupportRequest Request, bool NamedSurfaceOnly, MapExactPoint World)? BindingRequest(MapScopedSurfaces view,
        MapSupportBinding binding, MapExactXz xz)
    {
        var world = new MapExactPoint(xz.X, MapExactValue.FromSingle(binding.ReferenceY ?? 0), xz.Z);
        return binding switch
        {
            { Kind: MapSupportBindingKind.Surface, SurfaceId: { } surface } =>
                (new(MapEffectiveGeometry.Presentation(view, world), surface, null, null, 0, 0, null), true, world),
            { Kind: MapSupportBindingKind.Space, SpaceId: { } space, ReferenceY: not null, SearchBelow: { } below, SearchAbove: { } above } =>
                (new(MapEffectiveGeometry.Presentation(view, world), null, space, null, above, below, null), false, world),
            _ => null,
        };
    }

    internal static MapSupportResult Refusal(MapScopedSurfaces view, MapSupportStatus status, string detail) =>
        new(status, null, 0, null, null, null, null, detail, view.ReadWitness)
        { Capacity = status == MapSupportStatus.CapacityExceeded ? view.Capacity : null };

    internal static Outcome Compute(MapPreparedSupport prepared, MapSupportRequest request, MapBoundFaceWork? work, bool sampleSurface,
        MapExactPoint? exactWorld = null) => Compute(prepared, request, work, sampleSurface, exactWorld, null);

    /// <summary>The kernel over a supplied context prepared once for many points. Each point keeps its released budgets:
    /// its own demand set must fit the inspected-face allowance left by the prisms, and its intersections across every
    /// support floor must fit MaxSupportIntersections. Without a supplied context the point prepares its own, as released.</summary>
    internal static Outcome Compute(MapPreparedSupport prepared, MapSupportRequest request, MapBoundFaceWork? work, bool sampleSurface,
        MapExactPoint? exactWorld, MapBoundFaceContext? shared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(request);
        MapScopedSurfaces view = prepared.View;
        MapSpaceMembership spaces = prepared.Membership;
        MapFramePoint point = request.Point;
        if (point.Frame != view.Scope.Frame) throw new ArgumentException("point frame differs from acquired frame", nameof(request));
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z) ||
            !float.IsFinite(request.MaxStepUp) || request.MaxStepUp < 0 ||
            !float.IsFinite(request.MaxDropDown) || request.MaxDropDown < 0 ||
            (request.MaxSlopeRadians is { } slope && (!float.IsFinite(slope) || slope < 0 || slope > MathF.PI / 2)))
            return Failure(MapSupportStatus.Invalid, "invalid point or support interval or slope");
        if (sampleSurface && request.SurfaceId is null) return Failure(MapSupportStatus.Invalid, "named surface mode requires a surfaceId");
        if (view.Status == MapAcquireStatus.CapacityExceeded) return Failure(MapSupportStatus.CapacityExceeded, view.Detail);
        if (view.Status == MapAcquireStatus.NotRepresentable) return Failure(MapSupportStatus.NotRepresentable, view.Detail);
        if (view.Status == MapAcquireStatus.Incomplete) return Failure(MapSupportStatus.MissingGeometry, view.Detail ?? "unavailable acquired dependency");
        try
        {
            MapExactXz xz = exactWorld is { } exactInput ? new(exactInput.X, exactInput.Z) : point.ExactWorldXz();
            MapExactValue y = exactWorld?.Y ?? point.ExactY();
            MapExactValue low = y.Subtract(MapExactValue.FromSingle(request.MaxDropDown));
            MapExactValue high = y.Add(MapExactValue.FromSingle(request.MaxStepUp));
            MapSurfaceScope scope = view.Scope;
            if (exactWorld is { } supplied ? !MapEffectiveGeometry.InScope(view, supplied) : point.Local.X < scope.LocalMin.X || point.Local.X > scope.LocalMax.X ||
                point.Local.Z < scope.LocalMin.Y || point.Local.Z > scope.LocalMax.Y ||
                point.Local.Y < scope.MinY || point.Local.Y > scope.MaxY)
                return Failure(MapSupportStatus.MissingGeometry, "outside acquired scope");

            var cells = new List<MapCellDemand>();
            foreach (MapSurfaceRef surface in prepared.SupportFloors)
            {
                foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(view, surface.Id, xz))
                {
                    MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                    if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
                    {
                        // A complete acquisition reads every patch that meets its scope, and the point lies inside the
                        // scope. A floor captured through incident records or an empty neighbouring slot therefore holds
                        // no cell here when its slot was never read. A recorded unavailable read still refuses.
                        if (!view.HasRead(key)) continue;
                        return Failure(MapSupportStatus.MissingGeometry, $"patch {key}: {status}");
                    }
                    if (patch is not null) cells.Add(new(key, MapLowerCellClassifier.SlotCell(x, z)));
                }
            }
            MapMembershipResult membership;
            MapBoundFaceContext? context;
            if (shared is null)
                membership = exactWorld is { } exactPoint
                    ? spaces.QueryExact(exactPoint, cells.Select(c => new MapBoundFaceDemand(c, null)), work, out context)
                    : spaces.Query(point, cells.Select(c => new MapBoundFaceDemand(c, null)), work, out context);
            else
                membership = exactWorld is { } exactPoint
                    ? spaces.QueryExact(exactPoint, shared, cells.Select(c => new MapBoundFaceDemand(c, null)), out context)
                    : spaces.Query(point, shared, cells.Select(c => new MapBoundFaceDemand(c, null)), out context);
            if (context is null) return Failure(Status(membership.Status), membership.Detail);
            var intersections = new List<Intersection>();
            foreach (MapCellDemand cell in cells)
            {
                Intersection? selected = null;
                foreach (MapBoundFace face in context.Faces(cell))
                {
                    if (MapSubdividedTriangle.Height(face.Triangle, xz.X, xz.Z) is not { } height) continue;
                    if (selected is null || face.Key == request.CurrentSupport ||
                        (selected.Face.Key != request.CurrentSupport && face.Key.CompareTo(selected.Face.Key) < 0))
                        selected = new(context.CompiledFace(face.Key), height);
                }
                if (selected is null) continue;
                if (DemandDiagnostics.Check(view, "MapSupportSelection.MaxSupportIntersections.1", "MaxSupportIntersections", intersections.Count, 1, scope.Limits.MaxSupportIntersections, intersections.Count >= scope.Limits.MaxSupportIntersections, CapacityDemandLifetime.Inventory))
                    return Failure(MapSupportStatus.CapacityExceeded, "support intersections");
                intersections.Add(selected);
            }
            if (membership.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside))
                return Failure(Status(membership.Status), membership.Detail);
            MapTopologyRecord[] records = prepared.Records;
            MapSpaceFootprint[] footprints = prepared.Footprints
                .Where(f => MapSpaceGeometry.FootprintCell(view, f, xz) is not null).ToArray();
            if (sampleSurface)
            {
                Intersection? face = intersections.FirstOrDefault(i => i.Face.Key.OwnerId == request.SurfaceId);
                if (face is null) return Failure(MapSupportStatus.NoSupport, "surface has no physical support at XZ");
                MapSpaceFootprint? footprint = footprints.FirstOrDefault(f => f.Lower.SurfaceId == request.SurfaceId);
                string? owner = footprint is null ? null : MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space));
                if (records.OfType<MapPrismDomain>().Any())
                {
                    var occupiedFace = spaces.QueryExact(new(xz.X, face.Height, xz.Z), context);
                    if (occupiedFace.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside))
                        return Failure(Status(occupiedFace.Status), occupiedFace.Detail);
                    owner = occupiedFace.SpaceId;
                }
                var candidate = new Candidate(face.Face, face.Height, owner, null);
                return new(Supported(candidate), new[] { candidate });
            }
            string? spaceId = request.SpaceId ?? membership.SpaceId;
            if (spaceId is null) return Failure(MapSupportStatus.NoSupport, "outside every space");
            MapSpaceDoc space = records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == spaceId)
                ?? throw new MapDocumentException($"missing geometry: space '{spaceId}'");
            string ownerId = MapSpaceGeometry.Owner(view, space);
            Dictionary<string, MapRecordRef?> reachable = Reachable(view, spaces, ownerId, footprints, records, xz);
            var eligible = new List<Candidate>();
            IReadOnlyList<MapSpaceBoundsPart> occupied = Array.Empty<MapSpaceBoundsPart>();
            if (space.Kind == MapSpaceKind.Room)
            {
                var column = spaces.Column(point, exactWorld, context, out occupied);
                if (column.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside))
                    return Failure(Status(column.Status), column.Detail);
            }
            foreach (Intersection intersection in intersections)
            {
                if (request.SurfaceId is not null && intersection.Face.Key.OwnerId != request.SurfaceId) continue;
                if (space.Kind == MapSpaceKind.Room && occupied.Any(p => p.Owner == ownerId &&
                    intersection.Height.CompareTo(p.Lower) >= 0 && (p.Upper is not { } top || intersection.Height.CompareTo(top) < 0)))
                {
                    var sample = spaces.QueryExact(new(xz.X, intersection.Height, xz.Z), context);
                    if (sample.Status != MapMembershipStatus.Resolved) return Failure(Status(sample.Status), sample.Detail);
                    if (sample.SpaceId == ownerId && intersection.Height.CompareTo(low) >= 0 && intersection.Height.CompareTo(high) <= 0 &&
                        intersection.Face.Normal.Y > 0 && (request.MaxSlopeRadians is not { } roomSlope || MathF.Acos(Math.Clamp(intersection.Face.Normal.Y, -1, 1)) <= roomSlope))
                        eligible.Add(new(intersection.Face, intersection.Height, ownerId, null));
                    continue;
                }
                foreach (MapSpaceFootprint footprint in footprints)
                {
                    if (footprint.Lower.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1) ||
                        footprint.Lower.SurfaceId != intersection.Face.Key.OwnerId) continue;
                    string owner = MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space));
                    if (!reachable.TryGetValue(owner, out MapRecordRef? via)) continue;
                    if (intersection.Height.CompareTo(low) < 0 || intersection.Height.CompareTo(high) > 0 || intersection.Face.Normal.Y <= 0 ||
                        (request.MaxSlopeRadians is { } maxSlope && MathF.Acos(Math.Clamp(intersection.Face.Normal.Y, -1, 1)) > maxSlope)) continue;
                    eligible.Add(new(intersection.Face, intersection.Height, owner, via));
                    break;
                }
            }
            eligible.Sort((a, b) =>
            {
                int order = b.Height.CompareTo(a.Height);
                return order != 0 ? order : a.Face.Key.CompareTo(b.Face.Key);
            });
            if (eligible.Count != 0)
            {
                Candidate[] highest = eligible.TakeWhile(c => c.Height == eligible[0].Height).ToArray();
                if (!OneSeamOwner(view, highest, prepared.Seams, xz))
                    return new(Result(MapSupportStatus.Ambiguous, detail: "equal-height independent support owners"), eligible.AsReadOnly());
                Candidate selected = highest.FirstOrDefault(c => c.Face.Key == request.CurrentSupport) ?? highest[0];
                return new(Supported(selected), eligible.AsReadOnly());
            }
            if (membership.LowerCompatibility is { } tag && membership.SpaceId == ownerId &&
                (request.SurfaceId is null || request.SurfaceId == tag.Patch.SurfaceId))
            {
                MapValidatedSurfacePatch patch = context.ValidatedPatch(tag.Patch);
                MapExactValue exact = MapEffectiveGeometry.LegacyHeight(view, patch, tag.SlotCell, xz);
                float height = exact.ToSingle();
                if (exact.CompareTo(low) >= 0 && exact.CompareTo(high) <= 0)
                    return new(Result(MapSupportStatus.LegacyFallback, worldY: height, compatibility: tag, spaceId: ownerId), Array.Empty<Candidate>());
            }
            return Failure(MapSupportStatus.NoSupport, "no eligible support in the selected space");
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Failure(MapSupportStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return Failure(MapCommonRefinement.NotRepresentable(error) ? MapSupportStatus.NotRepresentable :
                error.Message.Contains("invalid", StringComparison.Ordinal) || error.Message.Contains("legacy recipe", StringComparison.Ordinal)
                    ? MapSupportStatus.Invalid : MapSupportStatus.MissingGeometry, error.Message);
        }

        MapSupportResult Supported(Candidate candidate) => Result(MapSupportStatus.Supported, candidate.Face.Key,
            candidate.Height.ToSingle(), candidate.Face.Normal, spaceId: candidate.SpaceId, via: candidate.Via);
        Outcome Failure(MapSupportStatus status, string? detail) => new(Result(status, detail: detail), Array.Empty<Candidate>());
        MapSupportResult Result(MapSupportStatus status, MapFaceKey? face = null, float worldY = 0, Vector3? normal = null,
            MapLegacyCellTag? compatibility = null, string? spaceId = null, MapRecordRef? via = null, string? detail = null) =>
            new(status, face, worldY, normal, compatibility, spaceId, via, detail, view.ReadWitness)
            { Capacity = status == MapSupportStatus.CapacityExceeded ? view.Capacity : null };
    }

    internal static Dictionary<string, MapRecordRef?> Reachable(MapScopedSurfaces view, MapSpaceMembership spaces, string owner,
        MapSpaceFootprint[] footprints, MapTopologyRecord[] records, MapExactXz point)
    {
        var result = new Dictionary<string, MapRecordRef?>(StringComparer.Ordinal) { [owner] = null };
        bool changed;
        do
        {
            changed = false;
            foreach (MapTopologyRecord record in records)
            {
                MapRecordRef reference = view.Witness.Records.First(r => r.Id == record.Id);
                if (record is MapCavePortal portal && spaces.OnPortal(portal, point))
                {
                    string from = Owner(portal.FromSpace), to = Owner(portal.ToSpace);
                    if (result.ContainsKey(from)) changed |= result.TryAdd(to, reference);
                    if (result.ContainsKey(to)) changed |= result.TryAdd(from, reference);
                }
                if (record is not MapVerticalLink link ||
                    !(link.Openings.Any(r => OnOpening(view, MapSpaceGeometry.Record<MapHorizontalOpening>(view, r), point)) ||
                      link.Portals.Any(r => spaces.OnPortal(MapSpaceGeometry.Record<MapCavePortal>(view, r), point)))) continue;
                string upper = Owner(link.UpperSpace), lower = Owner(link.LowerSpace);
                bool incident = result.ContainsKey(upper) || result.ContainsKey(lower) || footprints.Any(f =>
                    result.ContainsKey(Owner(f.Space)) &&
                    (link.Openings.Contains(f.Lower.Opening!) || link.Openings.Contains(f.Upper.Opening!)));
                if (!incident) continue;
                changed |= result.TryAdd(upper, reference);
                changed |= result.TryAdd(lower, reference);
            }
        } while (changed);
        return result;

        string Owner(MapRecordRef reference) => MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, reference));
    }

    static bool OnOpening(MapScopedSurfaces view, MapHorizontalOpening opening, MapExactXz point)
    {
        MapSurfaceRef surface = MapSpaceGeometry.Surface(view, opening.Patch.SurfaceId);
        var (x, z) = MapSpaceGeometry.Cell(surface.Frame, point);
        return MapPatchKey.ForCell(surface.Id, x, z) == opening.Patch && opening.SlotCells.Contains(MapLowerCellClassifier.SlotCell(x, z));
    }

    internal static bool OneSeamOwner(MapScopedSurfaces view, Candidate[] candidates, IEnumerable<MapSurfaceSeam> seams, MapExactXz point)
    {
        var joined = new HashSet<MapPatchKey?> { candidates[0].Face.Key.Patch };
        MapSurfaceSeam[] incident = seams.Where(s => OnEdge(s.First) && OnEdge(s.Second)).ToArray();
        bool changed;
        do
        {
            changed = false;
            foreach (MapSurfaceSeam seam in incident)
            {
                if (joined.Contains(seam.First.Patch)) changed |= joined.Add(seam.Second.Patch);
                if (joined.Contains(seam.Second.Patch)) changed |= joined.Add(seam.First.Patch);
            }
        } while (changed);
        return candidates.All(c => joined.Contains(c.Face.Key.Patch));

        bool OnEdge(MapSurfaceEdgeRef edge) => MapSpaceGeometry.OnSegment(point,
            MapEffectiveGeometry.Vertex(view, edge.From), MapEffectiveGeometry.Vertex(view, edge.To));
    }

    static MapSupportStatus Status(MapMembershipStatus status) => status switch
    {
        MapMembershipStatus.Resolved => MapSupportStatus.Supported,
        MapMembershipStatus.Outside => MapSupportStatus.NoSupport,
        MapMembershipStatus.MissingGeometry => MapSupportStatus.MissingGeometry,
        MapMembershipStatus.Ambiguous => MapSupportStatus.Ambiguous,
        MapMembershipStatus.CapacityExceeded => MapSupportStatus.CapacityExceeded,
        MapMembershipStatus.NotRepresentable => MapSupportStatus.NotRepresentable,
        _ => MapSupportStatus.Invalid,
    };
}
