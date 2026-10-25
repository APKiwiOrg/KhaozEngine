using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

internal sealed record MapOccupiedPiece(string SpaceId, string FragmentId, MapOccupiedBoundary LowerBoundary,
    MapOccupiedBoundary UpperBoundary, IReadOnlyList<MapWideXz> Polygon, IReadOnlyList<MapBoundFace> LowerFaces,
    IReadOnlyList<MapBoundFace>? UpperFaces, MapExactValue? LowerPlane, MapExactValue? UpperPlane)
{
    internal IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    internal MapRecordRef? AuthoredParent { get; init; }
    internal MapWideRational Bottom(MapWideXz p) => LowerPlane is { } plane ? MapWideRational.From(plane) :
        MapSpaceGeometry.HeightWide(LowerFaces, p) ?? throw new MapDocumentException("missing geometry: occupied lower bound");
    internal MapWideRational? Top(MapWideXz p) => UpperPlane is { } plane ? MapWideRational.From(plane) : UpperFaces is null ? null :
        MapSpaceGeometry.HeightWide(UpperFaces, p) ?? throw new MapDocumentException("missing geometry: occupied upper bound");
    internal MapWidePoint Lift(MapWideXz p, bool upper) => new(p.X, upper ? Top(p)
        ?? throw new MapDocumentException("open top has no plane") : Bottom(p), p.Z);
}

/// <summary>One identified partition shared by membership, columns and downstream water geometry. Planes never
/// create support faces. Logical fragment identities exclude clipping geometry and semantic digests.</summary>
internal static class MapOccupiedPartitions
{
    internal static MapOccupiedFragment Fragment(string space, MapOccupiedBoundary lower, MapOccupiedBoundary upper,
        IReadOnlyList<string> path) => new(MapPrefabJson.Digest("kemap/occupied-fragment/1", new
        { Space = space, Lower = new[] { lower.OwnerId, lower.Role }, Upper = new[] { upper.OwnerId, upper.Role }, Path = path }), space, lower, upper);

    internal static MapOccupiedBoundary Boundary(MapScopedSurfaces view, string owner, string role)
    {
        string digest;
        var record = MapSpaceGeometry.Records(view).FirstOrDefault(r => r.Id == owner);
        // Space and surface IDs have separate namespaces. A canonical floor/ceiling belongs to its surface,
        // even when an exterior space uses that same text ID. Ancestor-space metadata still names the record.
        if (record is MapSpaceDoc && role is "canonical-floor" or "canonical-ceiling" && view.Surfaces.Any(s => s.Id == owner)) record = null;
        if (record is not null)
            digest = MapPrefabJson.Digest("kemap/occupied-boundary/1", new
            { Record = record, Pose = view.EffectiveContext?.Transform(MapPrefabChildKind.Record, owner)?.Transform });
        else
        {
            var surface = view.Surfaces.FirstOrDefault(s => s.Id == owner);
            digest = MapPrefabJson.Digest("kemap/occupied-boundary/1", new
            {
                Surface = surface,
                Pose = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, owner)?.Transform,
                Patches = view.Witness.Present.Where(p => p.Key.SurfaceId == owner).OrderBy(p => p.Key).Select(p => p.Value).ToArray()
            });
        }
        return new(owner, role, digest);
    }

    internal static MapSpaceBoundsPart Identify(MapScopedSurfaces view, MapSpaceBoundsPart part)
    {
        var lower = part.LowerBoundary ?? Boundary(view, part.LowerBoundaryId, "canonical-floor");
        var upper = part.UpperBoundary ?? Boundary(view, part.UpperBoundaryId, part.Upper is null ? "open-top" : "canonical-ceiling");
        return part with { LowerBoundary = lower, UpperBoundary = upper, Fragment = Fragment(part.SpaceId, lower, upper, part.Path) };
    }

    internal static IReadOnlyList<MapSpaceBoundsPart> At(MapScopedSurfaces view, MapExactXz xz,
        IReadOnlyList<MapSpaceBoundsPart> canonical, IReadOnlyList<MapPrismDomain> prisms)
    {
        if (prisms.Count > view.Scope.Limits.MaxRecordReads) throw new MapDocumentException("capacity exceeded: prism records");
        var raw = canonical.Select(p => Identify(view, p)).ToList();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prism in prisms.OrderBy(p => p.Id, StringComparer.Ordinal)) Add(prism, 0);
        var result = new List<MapSpaceBoundsPart>();
        foreach (var part in raw)
        {
            var cuts = raw.Where(child => child.Owner != part.Owner && Ancestor(view, part.SpaceId, child.SpaceId)).ToArray();
            result.AddRange(Carve(part, cuts).Select(p => Identify(view, p)));
        }
        return result.AsReadOnly();

        void Add(MapPrismDomain prism, int depth)
        {
            if (done.Contains(prism.Id)) return;
            if (depth > view.Scope.Limits.MaxRecordDepth) throw new MapDocumentException("capacity exceeded: prism parent depth");
            if (!visiting.Add(prism.Id)) throw new MapDocumentException("invalid prism parent cycle");
            MapPrismCoverage.RequireStorey(view, prism);
            var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Space);
            string parentOwner = MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Parent));
            if (space.Kind != MapSpaceKind.Room || space.Parent != prism.Parent) throw new MapDocumentException("missing geometry: prism parent mismatch");
            foreach (var parent in prisms.Where(p => MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, p.Space)) == parentOwner)) Add(parent, depth + 1);
            var polygon = MapPrismGeometry.Compile(view, prism);
            if (polygon.Contains(xz))
            {
                var parents = raw.Where(p => p.Owner == parentOwner).ToArray();
                if (parents.Length == 0) throw new MapDocumentException("missing geometry: prism parent footprint");
                var (low, high) = MapPrismGeometry.Heights(view, prism);
                foreach (var parent in parents)
                {
                    var lower = MapSpaceGeometry.Max(parent.Lower, low);
                    var upper = parent.Upper is { } top ? MapSpaceGeometry.Min(top, high) : high;
                    if (lower.CompareTo(upper) >= 0) continue;
                    bool parentLow = parent.Lower.CompareTo(low) > 0 || parent.Lower == low && MapSpaceGeometry.Direction(parent.LowerFaces, xz) >= 0;
                    bool parentHigh = parent.Upper is { } ceiling && (ceiling.CompareTo(high) < 0 || ceiling == high && MapSpaceGeometry.Direction(parent.UpperFaces, xz) <= 0);
                    var lo = parentLow ? parent.LowerBoundary! : Boundary(view, prism.Id, "prism-lower");
                    var hi = parentHigh ? parent.UpperBoundary! : Boundary(view, prism.Id, "prism-upper");
                    var part = new MapSpaceBoundsPart(MapSpaceGeometry.Owner(view, space), MapSpaceGeometry.Owner(view, space), lower, upper,
                        lo.OwnerId, hi.OwnerId, false)
                    {
                        LowerBoundary = lo,
                        UpperBoundary = hi,
                        LowerFaces = parentLow ? parent.LowerFaces : Array.Empty<MapBoundFace>(),
                        UpperFaces = parentHigh ? parent.UpperFaces : Array.Empty<MapBoundFace>(),
                        AuthoredParent = prism.Parent,
                        Path = Extend(parent.Path, prism.Parent.Id, parent.LowerBoundary!, parent.UpperBoundary!)
                    };
                    raw.Add(Identify(view, part));
                }
            }
            visiting.Remove(prism.Id); done.Add(prism.Id);
        }
    }

    internal static bool Ancestor(MapScopedSurfaces view, string ancestor, string child)
    {
        var space = MapSpaceGeometry.Records(view).OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == child)
            ?? throw new MapDocumentException("missing geometry: occupied space");
        return MapSpaceGeometry.Ancestors(view, space).Skip(1).Any(s => MapSpaceGeometry.Owner(view, s) == ancestor);
    }

    static IReadOnlyList<string> Extend(IReadOnlyList<string> path, string space, MapOccupiedBoundary lower, MapOccupiedBoundary upper) =>
        Array.AsReadOnly(path.Concat(new[] { space, lower.OwnerId, lower.Role, upper.OwnerId, upper.Role }).ToArray());

    static IEnumerable<MapSpaceBoundsPart> Carve(MapSpaceBoundsPart part, IReadOnlyList<MapSpaceBoundsPart> cuts)
    {
        var cursor = part.Lower;
        var boundary = part.LowerBoundary!;
        foreach (var cut in cuts.OrderBy(p => p.Lower).ThenBy(p => p.SpaceId, StringComparer.Ordinal))
        {
            if (cut.Upper is { } end && end.CompareTo(cursor) <= 0) continue;
            if (part.Upper is { } top && cut.Lower.CompareTo(top) >= 0) break;
            if (cut.Lower.CompareTo(cursor) > 0)
                yield return part with
                {
                    Lower = cursor,
                    LowerBoundaryId = boundary.OwnerId,
                    LowerBoundary = boundary,
                    Upper = cut.Lower,
                    UpperBoundaryId = cut.LowerBoundaryId,
                    UpperBoundary = cut.LowerBoundary
                };
            if (cut.Upper is not { } high) yield break;
            cursor = high; boundary = cut.UpperBoundary!;
        }
        if (part.Upper is not { } upper || cursor.CompareTo(upper) < 0)
            yield return part with { Lower = cursor, LowerBoundaryId = boundary.OwnerId, LowerBoundary = boundary };
    }

    internal static IReadOnlyList<MapOccupiedPiece> Pieces(MapScopedSurfaces view, string spaceId,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits) => Build(view, min, max, limits, carve: true)
        .Where(p => p.SpaceId == spaceId).ToArray();

    internal static IReadOnlyList<MapOccupiedPiece> Build(MapScopedSurfaces view, MapExactXz min, MapExactXz max,
        MapRefinementLimits limits, bool carve)
    {
        var result = BuildWide(view, min, max, limits, carve);
        if (result.Status != MapRefinementStatus.Complete) throw Refusal(result.Status, result.Detail);
        return result.Pieces;
    }

    internal static MapOccupiedPartitionResult BuildWide(MapScopedSurfaces view, MapExactXz min, MapExactXz max,
        MapRefinementLimits limits, bool carve, MapWaterWorkMeter? meter = null)
    {
        try { return new(MapRefinementStatus.Complete, BuildCore(view, min, max, limits, carve, meter), null, view.ReadWitness); }
        catch (Exception error) when (error is MapDocumentException or MapExactOverflowException or OverflowException)
        {
            var status = error.Message.Contains("capacity exceeded", StringComparison.Ordinal) || error.Message.Contains("domain work exceeds", StringComparison.Ordinal)
                ? MapRefinementStatus.CapacityExceeded : MapCommonRefinement.NotRepresentable(error) || error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase)
                    ? MapRefinementStatus.NotRepresentable : MapRefinementStatus.MissingGeometry;
            return new(status, [], error.Message, view.ReadWitness);
        }
    }

    static IReadOnlyList<MapOccupiedPiece> BuildCore(MapScopedSurfaces view, MapExactXz min, MapExactXz max,
        MapRefinementLimits limits, bool carve, MapWaterWorkMeter? meter)
    {
        if (!view.Witness.Complete) throw new MapDocumentException("missing geometry: occupied partition acquisition");
        var records = MapSpaceGeometry.Records(view).ToArray();
        var domains = records.OfType<MapPrismDomain>().ToArray();
        if (domains.Length > view.Scope.Limits.MaxRecordReads || domains.Sum(MapPrismGeometry.Cost) > limits.MaxRefinementVertices)
            throw new MapDocumentException("capacity exceeded: prism records or vertices");
        var footprints = records.OfType<MapSpaceFootprint>().ToArray();
        var prepared = MapFootprintCoveragePreparation.Prepare(view, footprints, min, max, limits, meter);
        if (prepared.Context is not { } context) throw new MapDocumentException("capacity exceeded: " + prepared.Refusal);
        var raw = new List<MapOccupiedPiece>();
        var parentIndexes = new Dictionary<string, (MapOccupiedPiece[] Parents, MapWaterPolygons.Bounds[] Bounds, MapBoundsIndex Index)>(StringComparer.Ordinal);
        var vertices = new Dictionary<List<MapOccupiedPiece>, long>();
        var work = new MapWaterWorkBudget(limits.MaxPairChecks, compile: false, meter?.Pairs);
        foreach (var footprint in footprints)
        {
            var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space);
            foreach (int cell in footprint.SlotCells)
            {
                var authored = MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, cell);
                var polygon = MapWideRefinementGeometry.Normalize(authored.Select(MapWideXz.From));
                var extent = MapEffectiveGeometry.Bounds(authored);
                if (extent.MaxX.CompareTo(min.X) <= 0 || extent.MinX.CompareTo(max.X) >= 0 ||
                    extent.MaxZ.CompareTo(min.Z) <= 0 || extent.MinZ.CompareTo(max.Z) >= 0) continue;
                var rect = extent;
                var lower = MapCommonRefinement.BoundFaces(context, footprint, footprint.Lower, new(rect.MinX, rect.MinZ), new(rect.MaxX, rect.MaxZ), limits);
                var upper = footprint.Upper.Kind == MapBoundKind.OpenTop ? null :
                    MapCommonRefinement.BoundFaces(context, footprint, footprint.Upper, new(rect.MinX, rect.MinZ), new(rect.MaxX, rect.MaxZ), limits);
                Require(lower); if (upper is not null) Require(upper);
                var refined = MapCommonRefinement.RefineWide(lower.Faces, upper?.Faces, polygon, limits, budget: meter is null ? null : work);
                if (refined.Status != MapRefinementStatus.Complete) throw Refusal(refined.Status, refined.Detail);
                if (!refined.LowerArea.Equals(refined.CellArea) || upper is not null && !refined.UpperArea!.Value.Equals(refined.CellArea))
                    throw new MapDocumentException("missing geometry: canonical occupied bounds");
                foreach (var face in refined.Faces)
                {
                    var lo = Boundary(view, footprint.Lower.SurfaceId ?? footprint.Lower.Opening!.Id, "canonical-floor");
                    var hi = Boundary(view, footprint.Upper.SurfaceId ?? footprint.Upper.Opening?.Id ?? "open-top",
                        upper is null ? "open-top" : "canonical-ceiling");
                    Add(raw, Make(MapSpaceGeometry.Owner(view, space), lo, hi, face.Polygon,
                        new[] { lower.Faces.Single(f => f.Key == face.Lower) }, face.Upper is { } key ? new[] { upper!.Faces.Single(f => f.Key == key) } : null, null, null, []));
                }
            }
        }
        var prisms = records.OfType<MapPrismDomain>().ToArray();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prism in prisms.OrderBy(p => p.Id, StringComparer.Ordinal)) Room(prism, 0);
        if (!carve) return raw.AsReadOnly();
        var children = new Dictionary<int, List<MapOccupiedPiece>>();
        var ancestry = new Dictionary<(string, string), bool>();
        foreach (var (a, b) in MapWaterBoundsSweep.Pairs(raw.Select(p => MapWaterPolygons.Bounds.Of(p.Polygon)).ToArray(), false, work))
        {
            if (raw[a].SpaceId == raw[b].SpaceId) continue;
            if (IsAncestor(raw[a].SpaceId, raw[b].SpaceId)) Candidate(a, raw[b]);
            if (IsAncestor(raw[b].SpaceId, raw[a].SpaceId)) Candidate(b, raw[a]);
        }
        var result = new List<MapOccupiedPiece>();
        for (int index = 0; index < raw.Count; index++)
        {
            var part = raw[index];
            var pieces = new List<MapOccupiedPiece> { part };
            foreach (var child in (children.GetValueOrDefault(index) ?? []).OrderBy(p => p.SpaceId, StringComparer.Ordinal).ThenBy(p => p.Polygon[0]))
            {
                var next = new List<MapOccupiedPiece>();
                foreach (var piece in pieces)
                {
                    work.Charge(1);
                    var inside = Intersect(piece.Polygon, child.Polygon);
                    if (!Positive(inside)) { Add(next, piece); continue; }
                    if (child.UpperFaces is not null || child.UpperPlane is not null)
                        inside = Clip(inside, p => Height(child, p, true).Subtract(Height(piece, p, false)));
                    if (piece.UpperFaces is not null || piece.UpperPlane is not null)
                        inside = Clip(inside, p => Height(piece, p, true).Subtract(Height(child, p, false)));
                    if (!Positive(inside) ||
                        (child.UpperFaces is not null || child.UpperPlane is not null) && !inside.Any(p => Height(child, p, true).CompareTo(Height(piece, p, false)) > 0) ||
                        (piece.UpperFaces is not null || piece.UpperPlane is not null) && !inside.Any(p => Height(piece, p, true).CompareTo(Height(child, p, false)) > 0))
                    { Add(next, piece); continue; }
                    foreach (var outside in Difference(piece.Polygon, inside)) Add(next, piece with { Polygon = outside });
                    var below = Clip(inside, p => Height(child, p, false).Subtract(Height(piece, p, false)));
                    if (Positive(below) && below.Any(p => Height(child, p, false).CompareTo(Height(piece, p, false)) > 0)) Add(next, Make(piece.SpaceId, piece.LowerBoundary, child.LowerBoundary, below,
                        piece.LowerFaces, child.LowerFaces, piece.LowerPlane, child.LowerPlane, piece.Path) with
                    { AuthoredParent = piece.AuthoredParent });
                    if (child.UpperFaces is not null || child.UpperPlane is not null)
                    {
                        var above = piece.UpperFaces is null && piece.UpperPlane is null ? inside :
                            Clip(inside, p => Height(piece, p, true).Subtract(Height(child, p, true)));
                        if (Positive(above) && (piece.UpperFaces is null && piece.UpperPlane is null || above.Any(p => Height(piece, p, true).CompareTo(Height(child, p, true)) > 0))) Add(next, Make(piece.SpaceId, child.UpperBoundary, piece.UpperBoundary, above,
                            child.UpperFaces ?? [], piece.UpperFaces, child.UpperPlane, piece.UpperPlane, piece.Path) with
                        { AuthoredParent = piece.AuthoredParent });
                    }
                }
                pieces = next;
            }
            foreach (var piece in pieces) Add(result, piece);
        }
        return result.Select(piece =>
        {
            var polygon = Clip(piece.Polygon, p => p.X.Subtract(W(min.X)));
            polygon = Clip(polygon, p => W(max.X).Subtract(p.X));
            polygon = Clip(polygon, p => p.Z.Subtract(W(min.Z)));
            polygon = Clip(polygon, p => W(max.Z).Subtract(p.Z));
            return piece with { Polygon = MapWideRefinementGeometry.Normalize(polygon) };
        }).Where(p => Positive(p.Polygon)).ToArray();

        bool IsAncestor(string parent, string child)
        {
            if (!ancestry.TryGetValue((parent, child), out bool result)) ancestry[(parent, child)] = result = Ancestor(view, parent, child);
            return result;
        }
        void Candidate(int index, MapOccupiedPiece piece)
        {
            if (!children.TryGetValue(index, out var candidates)) children[index] = candidates = new();
            candidates.Add(piece);
        }
        void Add(List<MapOccupiedPiece> target, MapOccupiedPiece piece)
        {
            piece = piece with { Polygon = MapWideRefinementGeometry.Normalize(piece.Polygon) };
            long count = vertices.GetValueOrDefault(target) + piece.Polygon.Count;
            if (target.Count >= limits.MaxRefinementFaces || count > limits.MaxRefinementVertices)
                throw new MapDocumentException("capacity exceeded: occupied pieces");
            vertices[target] = count;
            target.Add(piece);
            if (ReferenceEquals(target, raw)) parentIndexes.Remove(piece.SpaceId);
        }
        // The owner's pieces in raw order with their bounds and index, rebuilt only after raw gains a piece of that owner.
        (MapOccupiedPiece[] Parents, MapWaterPolygons.Bounds[] Bounds, MapBoundsIndex Index) ParentIndex(string owner)
        {
            if (parentIndexes.TryGetValue(owner, out var cached)) return cached;
            var parents = raw.Where(p => p.SpaceId == owner).ToArray();
            var bounds = parents.Select(p => MapWaterPolygons.Bounds.Of(p.Polygon)).ToArray();
            return parentIndexes[owner] = (parents, bounds, new MapBoundsIndex(bounds));
        }
        void Room(MapPrismDomain prism, int depth)
        {
            if (done.Contains(prism.Id)) return;
            if (depth > view.Scope.Limits.MaxRecordDepth) throw new MapDocumentException("capacity exceeded: prism depth");
            if (!visiting.Add(prism.Id)) throw new MapDocumentException("invalid prism parent cycle");
            MapPrismCoverage.RequireStorey(view, prism);
            var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Space);
            string parentOwner = MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Parent));
            if (space.Kind != MapSpaceKind.Room || space.Parent != prism.Parent) throw new MapDocumentException("missing geometry: prism parent mismatch");
            foreach (var parent in prisms.Where(p => MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, p.Space)) == parentOwner)) Room(parent, depth + 1);
            var domain = MapPrismGeometry.Compile(view, prism);
            var (lower, upper) = MapPrismGeometry.Heights(view, prism);
            var lo = Boundary(view, prism.Id, "prism-lower"); var hi = Boundary(view, prism.Id, "prism-upper");
            var (parents, parentBounds, parentIndex) = ParentIndex(parentOwner);
            // Every convex part is prepared before the first parent, as released. The owner's cached exact bounds index
            // then gives, in the released parent, piece and part order, only the parts whose bounds overlap a parent.
            // The others share no positive area. Each visited pair is charged once.
            var parts = parents.Length == 0 ? [] : Enumerable.Range(0, domain.Pieces.Count)
                .Select(i => domain.ConvexParts(i, work).Select(c => (Polygon: c, Bounds: MapWaterPolygons.Bounds.Of(c))).ToArray()).ToArray();
            var near = Enumerable.Range(0, parts.Length).SelectMany(i => parentIndex.Overlapping(domain.PieceBounds(i)).Select(k => (Parent: k, Piece: i)))
                .Order().ToLookup(p => p.Parent, p => p.Piece);
            for (int k = 0; k < parents.Length; k++)
                foreach (int i in near[k])
                    foreach (var (convex, extent) in parts[i])
                    {
                        if (!parentBounds[k].OverlapsOpen(extent)) continue;
                        var parent = parents[k];
                        work.Charge(1);
                        var polygon = Intersect(parent.Polygon, convex);
                        if (!Positive(polygon)) continue;
                        foreach (bool parentLow in new[] { true, false })
                        {
                            var bottom = Clip(polygon, p => parentLow ? Height(parent, p, false).Subtract(W(lower)) : W(lower).Subtract(Height(parent, p, false)));
                            if (!Positive(bottom)) continue;
                            // When equal throughout, the canonical boundary owns the tie, as point membership does.
                            if (!parentLow && bottom.All(p => Height(parent, p, false).CompareTo(W(lower)) == 0)) continue;
                            foreach (bool parentHigh in new[] { true, false })
                            {
                                bool open = parent.UpperFaces is null && parent.UpperPlane is null;
                                if (open && parentHigh) continue;
                                var clipped = open ? bottom : Clip(bottom, p => parentHigh ? W(upper).Subtract(Height(parent, p, true)) : Height(parent, p, true).Subtract(W(upper)));
                                if (!Positive(clipped) || !open && !parentHigh && clipped.All(p => Height(parent, p, true).CompareTo(W(upper)) == 0)) continue;
                                var piece = Make(MapSpaceGeometry.Owner(view, space), parentLow ? parent.LowerBoundary : lo, parentHigh ? parent.UpperBoundary : hi,
                                    clipped, parentLow ? parent.LowerFaces : [], parentHigh ? parent.UpperFaces : null,
                                    parentLow ? parent.LowerPlane : lower, parentHigh ? parent.UpperPlane : upper,
                                    Extend(parent.Path, prism.Parent.Id, parent.LowerBoundary, parent.UpperBoundary)) with
                                { AuthoredParent = prism.Parent };
                                clipped = Clip(clipped, p => Height(piece, p, true).Subtract(Height(piece, p, false)));
                                if (Positive(clipped) && clipped.Any(p => Height(piece, p, true).CompareTo(Height(piece, p, false)) > 0)) Add(raw, piece with { Polygon = clipped });
                            }
                        }
                    }
            visiting.Remove(prism.Id); done.Add(prism.Id);
        }
    }

    static MapOccupiedPiece Make(string space, MapOccupiedBoundary lower, MapOccupiedBoundary upper, IReadOnlyList<MapWideXz> polygon,
        IReadOnlyList<MapBoundFace> lowerFaces, IReadOnlyList<MapBoundFace>? upperFaces, MapExactValue? lowerPlane, MapExactValue? upperPlane,
        IReadOnlyList<string> path) => new(space, Fragment(space, lower, upper, path).Id, lower, upper,
            MapWideRefinementGeometry.Normalize(polygon), lowerFaces, upperFaces, lowerPlane, upperPlane)
        { Path = path };
    internal static bool Positive(IReadOnlyList<MapWideXz> polygon) => polygon.Count >= 3 && MapEffectiveGeometry.AreaWide(polygon).Sign != 0;
    internal static MapWideRational Height(MapOccupiedPiece piece, MapWideXz point, bool upper) => upper ? piece.Top(point)
        ?? throw new MapDocumentException("open top has no plane") : piece.Bottom(point);
    internal static List<MapWideXz> Clip(IReadOnlyList<MapWideXz> polygon, Func<MapWideXz, MapWideRational> distance)
    {
        var result = new List<MapWideXz>();
        if (polygon.Count == 0) return result;
        var previous = polygon[^1]; var before = distance(previous);
        foreach (var current in polygon)
        {
            var after = distance(current);
            if ((before.Sign < 0) != (after.Sign < 0))
            {
                var t = before.Divide(before.Subtract(after));
                result.Add(new(previous.X.Add(current.X.Subtract(previous.X).Multiply(t)),
                    previous.Z.Add(current.Z.Subtract(previous.Z).Multiply(t))));
            }
            if (after.Sign >= 0) result.Add(current);
            previous = current; before = after;
        }
        return result.Distinct().ToList();
    }
    internal static List<MapWideXz> Intersect(IReadOnlyList<MapWideXz> a, IReadOnlyList<MapWideXz> b) =>
        MapEffectiveGeometry.Intersect(a, b);
    internal static IEnumerable<IReadOnlyList<MapWideXz>> Difference(IReadOnlyList<MapWideXz> polygon, IReadOnlyList<MapWideXz> clip)
    {
        var remaining = polygon.ToList();
        for (int i = 0; i < clip.Count && remaining.Count != 0; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];
            var outside = Clip(remaining, p => MapWaterPolygons.Cross(a, b, p).Negate());
            if (Positive(outside)) yield return outside;
            remaining = Clip(remaining, p => MapWaterPolygons.Cross(a, b, p));
        }
    }
    internal static MapDocumentException Refusal(MapRefinementStatus status, string? detail) => new(status switch
    {
        MapRefinementStatus.CapacityExceeded => "capacity exceeded: " + detail,
        MapRefinementStatus.NotRepresentable => "not representable: " + detail,
        _ => "missing geometry: " + detail
    });
    static void Require(MapBoundFaceSet bound) { if (bound.Status != MapRefinementStatus.Complete) throw Refusal(bound.Status, bound.Detail); }
    static MapWideRational W(MapExactValue value) => MapWideRational.From(value);
}
