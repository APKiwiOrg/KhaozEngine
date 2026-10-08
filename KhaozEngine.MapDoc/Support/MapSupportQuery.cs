using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Support;

/// <summary>A bounded vertical-column query over canonical support geometry and acquired space records.</summary>
public sealed class MapSupportQuery
{
    readonly MapScopedSurfaces _view;
    readonly MapSpaceMembership _membership;

    public MapSupportQuery(MapScopedSurfaces scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        _view = scoped;
        _membership = new(scoped);
    }

    public MapSupportResult Select(MapSupportRequest request) => Compute(request, null, false).Result;
    internal MapSupportResult Select(MapSupportRequest request, MapBoundFaceWork work) => Compute(request, work, false).Result;

    // A surface binding samples its authored owner directly, without inventing a reference height or search interval.
    internal MapSupportResult SampleSurface(MapFramePoint point, string surfaceId) =>
        Compute(new(point, surfaceId, null, null, 0, 0, null), null, true).Result;

    public MapSupportCandidateSet EnumerateCandidates(MapSupportRequest request, Span<MapSupportCandidate> buffer)
    {
        Outcome outcome = Compute(request, null, false);
        int count = Math.Min(buffer.Length, outcome.Candidates.Count);
        for (int i = 0; i < count; i++)
        {
            Candidate candidate = outcome.Candidates[i];
            buffer[i] = new(candidate.Face.Key, candidate.SpaceId!, candidate.Height.ToSingle(), candidate.Face.Normal, candidate.Via);
        }
        return new(outcome.Candidates.Count > buffer.Length ? MapSupportStatus.CapacityExceeded : outcome.Result.Status,
            count, outcome.Candidates.Count, _view.ReadWitness);
    }

    sealed record Intersection(MapCompiledFace Face, MapExactValue Height);
    sealed record Candidate(MapCompiledFace Face, MapExactValue Height, string? SpaceId, MapRecordRef? Via);
    sealed record Outcome(MapSupportResult Result, IReadOnlyList<Candidate> Candidates);

    Outcome Compute(MapSupportRequest request, MapBoundFaceWork? work, bool sampleSurface)
    {
        ArgumentNullException.ThrowIfNull(request);
        MapFramePoint point = request.Point;
        if (point.Frame != _view.Scope.Frame) throw new ArgumentException("point frame differs from acquired frame", nameof(request));
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z) ||
            !float.IsFinite(request.MaxStepUp) || request.MaxStepUp < 0 ||
            !float.IsFinite(request.MaxDropDown) || request.MaxDropDown < 0 ||
            (request.MaxSlopeRadians is { } slope && (!float.IsFinite(slope) || slope < 0 || slope > MathF.PI / 2)))
            return Failure(MapSupportStatus.Invalid, "invalid point or support interval or slope");
        if (_view.Status == MapAcquireStatus.CapacityExceeded) return Failure(MapSupportStatus.CapacityExceeded, _view.Detail);
        if (_view.Status == MapAcquireStatus.NotRepresentable) return Failure(MapSupportStatus.NotRepresentable, _view.Detail);
        if (_view.Status == MapAcquireStatus.Incomplete) return Failure(MapSupportStatus.MissingGeometry, _view.Detail ?? "unavailable acquired dependency");
        try
        {
            MapExactXz xz = point.ExactWorldXz();
            MapExactValue y = point.ExactY();
            MapExactValue low = y.Subtract(MapExactValue.FromSingle(request.MaxDropDown));
            MapExactValue high = y.Add(MapExactValue.FromSingle(request.MaxStepUp));
            MapSurfaceScope scope = _view.Scope;
            if (point.Local.X < scope.LocalMin.X || point.Local.X > scope.LocalMax.X ||
                point.Local.Z < scope.LocalMin.Y || point.Local.Z > scope.LocalMax.Y ||
                point.Local.Y < scope.MinY || point.Local.Y > scope.MaxY)
                return Failure(MapSupportStatus.MissingGeometry, "outside acquired scope");

            var cells = new List<MapCellDemand>();
            foreach (MapSurfaceRef surface in _view.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var (x, z) = MapSpaceGeometry.Cell(surface.Frame, xz);
                MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
                    return Failure(MapSupportStatus.MissingGeometry, $"patch {key}: {status}");
                if (patch is not null) cells.Add(new(key, MapLowerCellClassifier.SlotCell(x, z)));
            }
            MapMembershipResult membership = _membership.Query(point, cells.Select(c => new MapBoundFaceDemand(c, null)), work, out MapBoundFaceContext? context);
            if (context is null) return Failure(Status(membership.Status), membership.Detail);
            var intersections = new List<Intersection>();
            foreach (MapCellDemand cell in cells)
            {
                MapCompiledPatch compiled = context.Compiled(cell.Patch);
                var normals = compiled.Faces.Where(f => f.Key.Primitive == cell.SlotCell).ToDictionary(f => f.Key);
                Intersection? selected = null;
                foreach (MapBoundFace face in context.Faces(cell))
                {
                    if (MapSubdividedTriangle.Height(face.Triangle, xz.X, xz.Z) is not { } height) continue;
                    if (selected is null || face.Key == request.CurrentSupport ||
                        (selected.Face.Key != request.CurrentSupport && face.Key.CompareTo(selected.Face.Key) < 0))
                        selected = new(normals[face.Key], height);
                }
                if (selected is null) continue;
                if (intersections.Count >= scope.Limits.MaxSupportIntersections)
                    return Failure(MapSupportStatus.CapacityExceeded, "support intersections");
                intersections.Add(selected);
            }
            if (membership.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside))
                return Failure(Status(membership.Status), membership.Detail);
            MapTopologyRecord[] records = MapSpaceGeometry.Records(_view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            MapSpaceFootprint[] footprints = records.OfType<MapSpaceFootprint>()
                .Where(f => MapSpaceGeometry.FootprintCell(_view, f, xz) is not null).ToArray();
            if (sampleSurface)
            {
                Intersection? face = intersections.FirstOrDefault(i => i.Face.Key.OwnerId == request.SurfaceId);
                if (face is null) return Failure(MapSupportStatus.NoSupport, "surface has no physical support at XZ");
                MapSpaceFootprint? footprint = footprints.FirstOrDefault(f => f.Lower.SurfaceId == request.SurfaceId);
                string? owner = footprint is null ? null : MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, footprint.Space));
                var candidate = new Candidate(face.Face, face.Height, owner, null);
                return new(Supported(candidate), new[] { candidate });
            }
            string? spaceId = request.SpaceId ?? membership.SpaceId;
            if (spaceId is null) return Failure(MapSupportStatus.NoSupport, "outside every space");
            MapSpaceDoc space = records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == spaceId)
                ?? throw new MapDocumentException($"missing geometry: space '{spaceId}'");
            string ownerId = MapSpaceGeometry.Owner(_view, space);
            Dictionary<string, MapRecordRef?> reachable = Reachable(ownerId, footprints, records, xz);
            var eligible = new List<Candidate>();
            foreach (Intersection intersection in intersections)
            {
                if (request.SurfaceId is not null && intersection.Face.Key.OwnerId != request.SurfaceId) continue;
                foreach (MapSpaceFootprint footprint in footprints)
                {
                    if (footprint.Lower.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1) ||
                        footprint.Lower.SurfaceId != intersection.Face.Key.OwnerId) continue;
                    string owner = MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, footprint.Space));
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
                if (!OneSeamOwner(highest, records.OfType<MapSurfaceSeam>(), xz))
                    return new(Result(MapSupportStatus.Ambiguous, detail: "equal-height independent support owners"), eligible.AsReadOnly());
                Candidate selected = highest.FirstOrDefault(c => c.Face.Key == request.CurrentSupport) ?? highest[0];
                return new(Supported(selected), eligible.AsReadOnly());
            }
            if (membership.LowerCompatibility is { } tag && membership.SpaceId == ownerId &&
                (request.SurfaceId is null || request.SurfaceId == tag.Patch.SurfaceId))
            {
                MapValidatedSurfacePatch patch = context.ValidatedPatch(tag.Patch);
                float height = MapLegacyBilinear.HeightMetres(patch.Surface, patch.Patch, xz);
                MapExactValue exact = MapExactValue.FromSingle(height);
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
    }

    Dictionary<string, MapRecordRef?> Reachable(string owner, MapSpaceFootprint[] footprints, MapTopologyRecord[] records, MapExactXz point)
    {
        var result = new Dictionary<string, MapRecordRef?>(StringComparer.Ordinal) { [owner] = null };
        bool changed;
        do
        {
            changed = false;
            foreach (MapTopologyRecord record in records)
            {
                MapRecordRef reference = _view.Witness.Records.First(r => r.Id == record.Id);
                if (record is MapCavePortal portal && OnPortal(portal, point))
                {
                    string from = Owner(portal.FromSpace), to = Owner(portal.ToSpace);
                    if (result.ContainsKey(from)) changed |= result.TryAdd(to, reference);
                    if (result.ContainsKey(to)) changed |= result.TryAdd(from, reference);
                }
                if (record is not MapVerticalLink link ||
                    !(link.Openings.Any(r => OnOpening(MapSpaceGeometry.Record<MapHorizontalOpening>(_view, r), point)) ||
                      link.Portals.Any(r => OnPortal(MapSpaceGeometry.Record<MapCavePortal>(_view, r), point)))) continue;
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

        string Owner(MapRecordRef reference) => MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, reference));
    }

    bool OnOpening(MapHorizontalOpening opening, MapExactXz point)
    {
        MapSurfaceRef surface = MapSpaceGeometry.Surface(_view, opening.Patch.SurfaceId);
        var (x, z) = MapSpaceGeometry.Cell(surface.Frame, point);
        return MapPatchKey.ForCell(surface.Id, x, z) == opening.Patch && opening.SlotCells.Contains(MapLowerCellClassifier.SlotCell(x, z));
    }

    bool OnPortal(MapCavePortal portal, MapExactXz point) => _membership.OnPortal(portal, point);

    bool OneSeamOwner(Candidate[] candidates, IEnumerable<MapSurfaceSeam> seams, MapExactXz point)
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
            MapSpaceGeometry.Surface(_view, edge.From.SurfaceId).Frame.WorldXz(edge.From.Address),
            MapSpaceGeometry.Surface(_view, edge.To.SurfaceId).Frame.WorldXz(edge.To.Address));
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

    MapSupportResult Supported(Candidate candidate) => Result(MapSupportStatus.Supported, candidate.Face.Key,
        candidate.Height.ToSingle(), candidate.Face.Normal, spaceId: candidate.SpaceId, via: candidate.Via);
    Outcome Failure(MapSupportStatus status, string? detail) => new(Result(status, detail: detail), Array.Empty<Candidate>());
    MapSupportResult Result(MapSupportStatus status, MapFaceKey? face = null, float worldY = 0, Vector3? normal = null,
        MapLegacyCellTag? compatibility = null, string? spaceId = null, MapRecordRef? via = null, string? detail = null) =>
        new(status, face, worldY, normal, compatibility, spaceId, via, detail, _view.ReadWitness);
}
