using System.Runtime.CompilerServices;
using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Clips and partitions paint in wide precision, then narrows only the published material polygons.</summary>
public static partial class MapPartialPaintCompiler
{
    public static MapPaintRegionSet Compile(MapScopedSurfaces view, string targetSurfaceId, MapRefinementLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        using var admitted = view.Scope.SceneAdmission?.Enter();
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSurfaceId);
        var budget = limits ?? new();
        if (view.Capacity is { } capacity)
            throw new MapDocumentException(capacity.Message, new Scene.MapSceneCapacityException(capacity));
        if (view.Status != MapAcquireStatus.Complete)
            throw new MapDocumentException((view.Status == MapAcquireStatus.Incomplete ? "MissingGeometry" : view.Status.ToString()) + ": " + view.Detail);
        if (!view.Scope.Roles.Contains(MapSurfaceRole.PaintOverride))
            throw new MapDocumentException("MissingGeometry: paint compilation requires complete acquired paint and target geometry.");
        var target = view.Surfaces.SingleOrDefault(s => s.Id == targetSurfaceId);
        if (target is null || target.Role == MapSurfaceRole.PaintOverride)
            throw new MapDocumentException("Invalid: paint target must be a declared physical surface.");
        var paints = view.Surfaces.Where(s => s.Role == MapSurfaceRole.PaintOverride && s.PaintTargetSurfaceId == targetSurfaceId &&
            (view.EffectiveContext is null || view.EffectiveContext.IsPartialPaint(s)))
            .OrderByDescending(s => s.PaintPriority).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        foreach (var surface in view.Surfaces) MapSurfaceJson.RequirePaintPriority(surface);
        if (paints.Length == 0) return new([], Digest([]));
        try
        {
            var result = CompileCore(view, target, paints, budget);
            result.PreparationOwnership = preparation?.Complete(checked(128L + result.Regions.Sum(r => 128L + 48L * r.Polygon.Count)));
            return result;
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapDocumentException("NotRepresentable: paint intersection cannot be emitted exactly. No regions were published.", error); }
    }

    // Work follows painted area. Paint sources compile first. Only target cells whose exact lattice rectangle openly
    // overlaps a source's bounds are counted and compiled, then an exact bounds sweep gives each target face its
    // candidate sources in source order. A pair check is charged per candidate, never per face and source of the whole
    // target. Slab, conflict and occlusion checks charge the same counter.
    static IReadOnlyList<MapPaintRegion> CompileRegions(MapScopedSurfaces view, MapSurfaceRef target, MapSurfaceRef[] paints, MapRefinementLimits limits)
    {
        using var work = new Work(limits);
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(512L + 128L * (view.Surfaces.Count + view.Witness.Present.Count)));
        var refs = paints.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var present = view.Witness.Present.Select(p => p.Key).Order().ToArray();
        var paintKeys = present.Where(k => refs.ContainsKey(k.SurfaceId)).ToArray();
        work.Require(paintKeys.Length, limits.MaxContextPatches, nameof(MapRefinementLimits.MaxContextPatches), "context patches");
        var inputs = paintKeys.Select(key =>
        {
            var patch = Acquired(view, key);
            var surface = refs[key.SurfaceId];
            var adapter = surface with { Role = MapSurfaceRole.SupportFloor, PaintPriority = 0 };
            return (Surface: surface, Adapter: adapter, Patch: patch,
                Faces: MapSurfaceCompiler.CountFaces(adapter, patch, MapSlotCellMask.All));
        }).ToArray();
        long paintCount = inputs.Sum(p => p.Faces);
        work.Require(paintCount, limits.MaxSourceFacesPerBound, nameof(MapRefinementLimits.MaxSourceFacesPerBound), "source faces");
        work.Require(paintCount, limits.MaxContextFaces, nameof(MapRefinementLimits.MaxContextFaces), "context faces");
        var source = CaptureSources(view, inputs, work);
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked((long)System.Runtime.CompilerServices.Unsafe.SizeOf<MapWaterPolygons.Bounds>() * source.Length));
        var sourceBounds = source.Select(p => MapWaterPolygons.Bounds.Of(p.Polygon)).ToArray();
        // The cell filter refuses at the first hit target slot past the patch allowance, before marking it.
        var cells = TargetCells(view, target, sourceBounds, present.Where(k => k.SurfaceId == target.Id).ToArray(),
            limits.MaxContextPatches - paintKeys.Length,
            new("MaxContextPatches", paintKeys.Length, limits.MaxContextPatches, limits));
        work.Require(cells is null ? (long)limits.MaxContextPatches + 1 : (long)paintKeys.Length + cells.Count, limits.MaxContextPatches, nameof(MapRefinementLimits.MaxContextPatches), "context patches");
        var targets = cells!.Select(entry => (Patch: Acquired(view, entry.Key), Cells: entry.Value)).ToArray();
        long targetCount = targets.Sum(p => MapSurfaceCompiler.CountFaces(target, p.Patch, p.Cells));
        work.Require(targetCount, limits.MaxSourceFacesPerBound, nameof(MapRefinementLimits.MaxSourceFacesPerBound), "source faces");
        work.Require(targetCount + paintCount, limits.MaxContextFaces, nameof(MapRefinementLimits.MaxContextFaces), "context faces");
        long copies = TargetCopyBytes(targetCount);
        Scene.MapSceneAdmission.Current?.ReserveBytes(copies);
        var faces = targets.SelectMany(p => CaptureTargets(view, target, p.Patch, p.Cells)).ToArray();
        Scene.MapSceneAdmission.Current?.ReleaseUnusedBytes(copies);
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked((long)System.Runtime.CompilerServices.Unsafe.SizeOf<MapWaterPolygons.Bounds>() * faces.Length));
        // Every candidate is charged below, so more candidates than the remaining allowance refuse before listing them.
        var pairs = MapWaterBoundsSweep.Cross(faces.Select(f => MapWaterPolygons.Bounds.Of(f.Parent)).ToArray(), sourceBounds, work.RemainingPairs, Scene.MapSceneAdmission.Current);
        work.Require(pairs is null ? (long)limits.MaxPairChecks + 1 : 0, limits.MaxPairChecks, nameof(MapRefinementLimits.MaxPairChecks), "pair checks");
        var regions = new List<MapPaintRegion>();
        for (int next = 0; next < pairs!.Count;)
        {
            int index = pairs[next].Left;
            var (face, triangle, parent) = faces[index];
            int end = next; long edges = 3;
            while (end < pairs.Count && pairs[end].Left == index) edges = checked(edges + source[pairs[end++].Right].Polygon.Count);
            work.Preparation.BeginFace(end - next, edges, limits);
            try
            {
                var candidates = new List<Source>();
                for (; next < pairs.Count && pairs[next].Left == index; next++)
                {
                    var paint = source[pairs[next].Right];
                    work.Pair();
                    var clipped = MapWideRefinementGeometry.Normalize(MapEffectiveGeometry.Intersect(paint.Polygon, parent));
                    if (MapEffectiveGeometry.AreaWide(clipped).Sign == 0) continue;
                    candidates.Add(paint with { Material = paint.Material with { Face = face.Key }, Polygon = clipped });
                    work.Polygons(candidates.Select(c => c.Polygon));
                }
                // Check authored conflicts before occlusion, including conflicts hidden by higher-priority paint.
                for (int i = 0; i < candidates.Count; i++)
                    for (int j = i + 1; j < candidates.Count && candidates[j].Priority == candidates[i].Priority; j++)
                    {
                        var a = candidates[i]; var b = candidates[j];
                        if (a.Material == b.Material) continue;
                        work.Pair();
                        if (BoundsOverlap(a.Polygon, b.Polygon) && MapEffectiveGeometry.AreaWide(MapEffectiveGeometry.Intersect(a.Polygon, b.Polygon)).Sign > 0)
                            throw new MapDocumentException($"Invalid: equal-priority paint '{a.Id}' and '{b.Id}' overlap with conflicting materials. Author an explicit priority.");
                    }
                var accepted = new List<Source>();
                foreach (var candidate in candidates)
                {
                    var pieces = new List<IReadOnlyList<MapWideXz>> { candidate.Polygon };
                    foreach (var owner in accepted)
                    {
                        var remaining = new List<IReadOnlyList<MapWideXz>>();
                        foreach (var piece in pieces)
                        {
                            work.Pair();
                            if (!BoundsOverlap(piece, owner.Polygon)) remaining.Add(piece);
                            else remaining.AddRange(Subtract(piece, owner.Polygon));
                            work.Polygons(remaining);
                        }
                        pieces = remaining;
                        if (pieces.Count == 0) break;
                    }
                    foreach (var piece in pieces)
                    {
                        accepted.Add(candidate with { Polygon = piece });
                        work.Polygons(accepted.Select(c => c.Polygon));
                    }
                }
                foreach (var region in accepted)
                {
                    work.Preparation.BeforeEmit(region.Polygon.Count);
                    try
                    {
                        var points = region.Polygon.Select(p => Lift(triangle, p)).ToArray();
                        work.Emit(points.Length);
                        regions.Add(new(face.Key, region.Id, region.Priority, region.Material, points));
                    }
                    finally { work.Preparation.AfterEmit(region.Polygon.Count); }
                }
            }
            finally { work.Preparation.EndFace(); }
        }
        return regions;
    }

    static IEnumerable<Source> PaintSources(MapScopedSurfaces view, MapSurfaceRef surface,
        IEnumerable<MapCompiledPatch> patches, Work work)
    {
        // Cancel internal material edges in local space before the one point freeze. Paint height is irrelevant.
        var compiled = patches.ToArray();
        work.Preparation.BeginSource(compiled.Sum(p => (long)p.Faces.Count));
        try
        {
            var materials = compiled.SelectMany(p => p.Faces.Select((face, i) =>
                (Material: p.Paint[i] with { Face = default }, Polygon: Polygon(p.ExactTriangle(face)))))
                .GroupBy(p => p.Material).ToArray();
            var boundaries = materials.Select(group =>
            {
                var edges = new HashSet<Edge>();
                foreach (var face in group)
                    for (int i = 0; i < face.Polygon.Count; i++)
                    {
                        var edge = new Edge(face.Polygon[i], face.Polygon[(i + 1) % face.Polygon.Count]);
                        if (!edges.Remove(new(edge.B, edge.A)) && !edges.Add(edge))
                            throw new MapDocumentException("Invalid: non-manifold paint material boundary.");
                    }
                return (group.Key, Edges: edges);
            }).ToArray();
            var junctions = boundaries.SelectMany(b => b.Edges.SelectMany(e => new[] { e.A, e.B }).Distinct())
                .GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var boundary in boundaries)
            {
                var edges = boundary.Edges;
                var incoming = edges.GroupBy(e => e.B).ToDictionary(g => g.Key, g => g.ToArray());
                var outgoing = edges.GroupBy(e => e.A).ToDictionary(g => g.Key, g => g.ToArray());
                foreach (var vertex in outgoing.Keys.Order().ToArray())
                {
                    if (junctions.Contains(vertex) || outgoing[vertex].Length != 1 || !incoming.TryGetValue(vertex, out var before) || before.Length != 1) continue;
                    var first = before[0]; var second = outgoing[vertex][0];
                    var cross = first.B.X.Subtract(first.A.X).Multiply(second.B.Z.Subtract(second.A.Z))
                        .Subtract(first.B.Z.Subtract(first.A.Z).Multiply(second.B.X.Subtract(second.A.X)));
                    if (cross.Sign != 0) continue;
                    var combined = new Edge(first.A, second.B);
                    edges.Remove(first); edges.Remove(second); edges.Add(combined);
                    outgoing[first.A] = outgoing[first.A].Select(e => e == first ? combined : e).ToArray();
                    incoming[second.B] = incoming[second.B].Select(e => e == second ? combined : e).ToArray();
                }
                var points = new Dictionary<MapWideXz, MapWideXz>();
                MapWideXz Freeze(MapWideXz local)
                {
                    if (points.TryGetValue(local, out var frozenPoint)) return frozenPoint;
                    // These are existing authored endpoints, never generated region intersections.
                    var point = MapWideRefinementGeometry.Narrow(local);
                    frozenPoint = MapWideXz.From(view.EffectiveContext?.Xz(surface.Id, point) ?? point);
                    points.Add(local, frozenPoint);
                    return frozenPoint;
                }
                var frozen = edges.OrderBy(e => e.A).ThenBy(e => e.B).Select(e => new Edge(Freeze(e.A), Freeze(e.B))).ToArray();
                // Even-odd slabs handle concave outlines and holes without inventing an independent paint floor.
                var levels = frozen.SelectMany(e => new[] { e.A.Z, e.B.Z }).Distinct().Order().ToArray();
                var polygons = new List<IReadOnlyList<MapWideXz>>();
                for (int i = 1; i < levels.Length; i++)
                {
                    var low = levels[i - 1]; var high = levels[i];
                    var middle = low.Add(high).Divide(MapWideRational.From(2));
                    var crossings = new List<(MapWideRational X, Edge Edge)>();
                    foreach (var edge in frozen)
                    {
                        work.Pair();
                        if (MapWideRational.Min(edge.A.Z, edge.B.Z).CompareTo(middle) < 0 &&
                            MapWideRational.Max(edge.A.Z, edge.B.Z).CompareTo(middle) > 0)
                            crossings.Add((AtZ(edge, middle), edge));
                    }
                    crossings.Sort((a, b) => a.X.CompareTo(b.X));
                    if (crossings.Count % 2 != 0) throw new MapDocumentException("Invalid: open paint material boundary.");
                    for (int j = 0; j < crossings.Count; j += 2)
                    {
                        var left = crossings[j].Edge; var right = crossings[j + 1].Edge;
                        work.Preparation.BeforeSourcePolygon();
                        var polygon = MapWideRefinementGeometry.Normalize(new[] { new MapWideXz(AtZ(left, low), low),
                        new(AtZ(right, low), low), new(AtZ(right, high), high), new(AtZ(left, high), high) });
                        if (MapEffectiveGeometry.AreaWide(polygon).Sign == 0) { work.Preparation.DiscardSourcePolygon(); continue; }
                        polygons.Add(polygon);
                        work.Polygons(polygons);
                    }
                }
                foreach (var polygon in polygons)
                {
                    work.Source(polygon.Count);
                    yield return new(surface.Id, surface.PaintPriority, boundary.Key, polygon);
                }
            }
        }
        finally { work.Preparation.EndSource(); }
    }

    static MapSurfacePatch Acquired(MapScopedSurfaces view, MapPatchKey key) =>
        view.TryAcquiredPatch(key, out var patch, out _) && patch is not null ? patch
            : throw new MapDocumentException("MissingGeometry: paint or target patch unavailable.");

    // Present target patches in key order, each with its cells whose exact lattice rectangle openly overlaps some
    // source's bounds. Patches without such a cell are never read or counted. A posed target keeps every cell, because
    // its lattice is reached only through the rounded pose, so the exact face sweep alone culls it.
    static IReadOnlyList<KeyValuePair<MapPatchKey, MapSlotCellMask>>? TargetCells(MapScopedSurfaces view, MapSurfaceRef target,
        IReadOnlyList<MapWaterPolygons.Bounds> sources, IReadOnlyList<MapPatchKey> keys, int maxSlots, MapCapacityDemandOrigin? patchOrigin = null) =>
        view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, target.Id) is not null
            ? keys.Select(key => KeyValuePair.Create(key, MapSlotCellMask.All)).ToList()
            : CandidateCells(target.Frame, sources, keys, maxSlots, patchOrigin);

    /// <summary>The cells of the present slots <paramref name="keys"/> whose exact lattice rectangle openly overlaps a
    /// source's bounds, in key order. Null, with no partial result, once more than <paramref name="maxSlots"/> distinct
    /// slots are hit. A source visits only the present slots in its range, and each hit slot costs one OR per row.</summary>
    internal static IReadOnlyList<KeyValuePair<MapPatchKey, MapSlotCellMask>>? CandidateCells(MapLatticeFrame frame,
        IReadOnlyList<MapWaterPolygons.Bounds> sources, IReadOnlyList<MapPatchKey> keys, int maxSlots, MapCapacityDemandOrigin? patchOrigin = null)
    {
        var rows = keys.GroupBy(key => key.SlotZ).OrderBy(g => g.Key)
            .Select(g => (Z: g.Key, X: g.Select(key => key.SlotX).Distinct().Order().ToArray())).ToArray();
        long[] rowZ = rows.Select(r => r.Z).ToArray();
        var hit = new Dictionary<(long SlotX, long SlotZ), ulong[]>();
        foreach (var bounds in sources)
        {
            var range = MapLatticeRanges.CellRange(frame, new(Whole(bounds.Min.X, false), Whole(bounds.Min.Z, false)),
                new(Whole(bounds.Max.X, true), Whole(bounds.Max.Z, true)));
            if (range.MaxXExclusive <= range.MinX || range.MaxZExclusive <= range.MinZ) continue;
            long minX = MapSurfaceRanges.Floor(range.MinX, 64), maxX = MapSurfaceRanges.Floor(range.MaxXExclusive - 1, 64);
            long minZ = MapSurfaceRanges.Floor(range.MinZ, 64), maxZ = MapSurfaceRanges.Floor(range.MaxZExclusive - 1, 64);
            for (int zi = LowerBound(rowZ, minZ); zi < rowZ.Length && rowZ[zi] <= maxZ; zi++)
            {
                long slotZ = rowZ[zi], originZ = slotZ * 64;
                int z0 = (int)(Math.Max(range.MinZ, originZ) - originZ), z1 = (int)(Math.Min(range.MaxZExclusive, originZ + 64) - originZ);
                long[] xs = rows[zi].X;
                for (int xi = LowerBound(xs, minX); xi < xs.Length && xs[xi] <= maxX; xi++)
                {
                    long slotX = xs[xi], originX = slotX * 64;
                    if (!hit.TryGetValue((slotX, slotZ), out var mask))
                    {
                        bool refused = hit.Count >= maxSlots;
                        if (patchOrigin is { } origin)
                            (origin with { Consumed = origin.Consumed + hit.Count })
                                .Report(hit, "MapPartialPaintCompiler.MaxContextPatches.1", 1, CapacityDemandLifetime.Inventory);
                        else DemandDiagnostics.Check(hit, "MapPartialPaintCompiler.MaxContextPatches.1", "MaxContextPatches", hit.Count, 1, maxSlots, refused, CapacityDemandLifetime.Inventory);
                        if (refused) return null;
                        hit.Add((slotX, slotZ), mask = new ulong[64]);
                    }
                    int x0 = (int)(Math.Max(range.MinX, originX) - originX), x1 = (int)(Math.Min(range.MaxXExclusive, originX + 64) - originX);
                    ulong span = (x1 - x0 == 64 ? ulong.MaxValue : (1UL << (x1 - x0)) - 1) << x0;
                    for (int z = z0; z < z1; z++) mask[z] |= span;
                }
            }
        }
        return keys.Where(key => hit.ContainsKey((key.SlotX, key.SlotZ)))
            .Select(key => KeyValuePair.Create(key, MapSlotCellMask.FromRows(hit[(key.SlotX, key.SlotZ)]))).ToList();
    }

    static int LowerBound(long[] sorted, long value)
    {
        int index = Array.BinarySearch(sorted, value);
        return index < 0 ? ~index : index;
    }

    // An exact bound, or the enclosing whole metre when the generated vertex is not representable.
    static MapExactValue Whole(MapWideRational value, bool ceiling)
    {
        if (value.TryNarrow(out var exact)) return exact;
        var quotient = BigInteger.DivRem(value.Numerator, value.Denominator, out var remainder);
        if (ceiling ? remainder.Sign > 0 : remainder.Sign < 0) quotient += ceiling ? 1 : -1;
        return new((long)quotient, 1);
    }

    static MapWideRational AtZ(Edge edge, MapWideRational z) => edge.A.X.Add(edge.B.X.Subtract(edge.A.X)
        .Multiply(z.Subtract(edge.A.Z).Divide(edge.B.Z.Subtract(edge.A.Z))));

    static IReadOnlyList<MapWideXz> Polygon(MapExactTriangle triangle) => MapWideRefinementGeometry.Normalize(new[]
    { MapWidePoint.From(triangle.A).Xz, MapWidePoint.From(triangle.B).Xz, MapWidePoint.From(triangle.C).Xz });

    static bool BoundsOverlap(IReadOnlyList<MapWideXz> a, IReadOnlyList<MapWideXz> b) =>
        a.Min(p => p.X).CompareTo(b.Max(p => p.X)) < 0 && b.Min(p => p.X).CompareTo(a.Max(p => p.X)) < 0 &&
        a.Min(p => p.Z).CompareTo(b.Max(p => p.Z)) < 0 && b.Min(p => p.Z).CompareTo(a.Max(p => p.Z)) < 0;

    static IEnumerable<IReadOnlyList<MapWideXz>> Subtract(IReadOnlyList<MapWideXz> polygon, IReadOnlyList<MapWideXz> clip)
    {
        var inside = polygon;
        for (int i = 0; i < clip.Count && inside.Count >= 3; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];
            var outside = MapWideRefinementGeometry.Normalize(MapEffectiveGeometry.ClipHalfPlane(inside, a, b, inside: false));
            if (MapEffectiveGeometry.AreaWide(outside).Sign > 0) yield return outside;
            inside = MapEffectiveGeometry.ClipHalfPlane(inside, a, b);
        }
    }

    // The only bounded adapter for generated paint geometry. No intermediate polygon is narrowed or rounded.
    static MapExactPoint Lift(MapExactTriangle triangle, MapWideXz point)
    {
        var wide = new MapWidePoint(point.X, MapEffectiveGeometry.HeightWide(triangle, point), point.Z);
        if (!wide.X.TryNarrow(out var x) || !wide.Y.TryNarrow(out var y) || !wide.Z.TryNarrow(out var z))
            throw new MapExactOverflowException();
        return new(x, y, z);
    }

    static string Digest(IReadOnlyList<MapPaintRegion> regions) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/paint-regions/1");
        foreach (var region in regions)
        {
            w.WriteStartArray(); w.WriteStringValue(region.Face.OwnerId);
            w.WriteStringValue(region.Face.Patch?.SurfaceId);
            w.WriteStringValue(region.Face.Patch?.SlotX.ToString(CultureInfo.InvariantCulture));
            w.WriteStringValue(region.Face.Patch?.SlotZ.ToString(CultureInfo.InvariantCulture));
            w.WriteNumberValue(region.Face.Primitive); w.WriteNumberValue(region.Face.ParentTriangle);
            w.WriteNumberValue(region.Face.Child); w.WriteNumberValue((byte)region.Face.Side);
            w.WriteStringValue(region.PaintId); w.WriteNumberValue(region.Priority);
            w.WriteNumberValue(region.Material.Underlay); w.WriteNumberValue(region.Material.Overlay);
            w.WriteBooleanValue(region.Material.OverlayCovers); w.WriteBooleanValue(region.Material.Feather);
            foreach (var p in region.Polygon)
            {
                w.WriteStartArray();
                foreach (var value in new[] { p.X, p.Y, p.Z })
                { w.WriteStringValue(value.Numerator.ToString(CultureInfo.InvariantCulture)); w.WriteStringValue(value.Denominator.ToString(CultureInfo.InvariantCulture)); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
    });

    sealed record Source(string Id, int Priority, MapPaintCoverage Material, IReadOnlyList<MapWideXz> Polygon);
    readonly record struct Edge(MapWideXz A, MapWideXz B);

    sealed class Work(MapRefinementLimits limits) : IDisposable
    {
        long _pairs, _regions, _vertices, _sources, _sourceVertices;
        internal MapPaintPreparation Preparation { get; } = new();
        public void Dispose() => Preparation.Dispose();
        internal void Require(long requested, long allowance, string name, string detail, [CallerMemberName] string caller = "")
        {
            bool accepted = requested <= allowance;
            if (DemandDiagnostics.IsObserved)
                DemandDiagnostics.Check(this, "MapPartialPaintCompiler." + caller, name, 0, requested, allowance, !accepted, allowanceSource: limits);
            if (accepted) return;
            var capacity = new Scene.MapSceneCapacity(name, requested, allowance);
            throw new MapDocumentException("CapacityExceeded: paint " + detail + " budget exceeded. " + capacity.Message,
                new Scene.MapSceneCapacityException(capacity));
        }
        internal void Pair() => Require(++_pairs, limits.MaxPairChecks, nameof(MapRefinementLimits.MaxPairChecks), "pair checks");
        internal long RemainingPairs => Math.Max(0, limits.MaxPairChecks - _pairs);
        internal void Source(int vertices)
        {
            _sourceVertices += vertices;
            Require(++_sources, limits.MaxRefinementFaces, nameof(MapRefinementLimits.MaxRefinementFaces), "source regions");
            Require(_sourceVertices, limits.MaxRefinementVertices, nameof(MapRefinementLimits.MaxRefinementVertices), "source regions");
            Preparation.PublishSource(vertices);
        }
        internal void Polygons(IEnumerable<IReadOnlyList<MapWideXz>> polygons)
        {
            long count = 0, vertices = 0;
            foreach (var p in polygons) { count++; vertices += p.Count; }
            Require(count, limits.MaxRefinementFaces, nameof(MapRefinementLimits.MaxRefinementFaces), "intermediate regions");
            Require(vertices, limits.MaxRefinementVertices, nameof(MapRefinementLimits.MaxRefinementVertices), "intermediate regions");
        }
        internal void Emit(int vertices)
        {
            _vertices += vertices;
            Require(++_regions, limits.MaxRefinementFaces, nameof(MapRefinementLimits.MaxRefinementFaces), "output regions");
            Require(_vertices, limits.MaxRefinementVertices, nameof(MapRefinementLimits.MaxRefinementVertices), "output regions");
        }
    }
}
