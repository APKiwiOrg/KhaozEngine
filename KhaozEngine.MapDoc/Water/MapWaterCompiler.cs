using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Compiles each water body of a set into free-surface, closed and unresolved regions against one
/// acquisition. Per body, in order, every limit is charged before the work it guards and refuses with
/// <see cref="MapWaterCompileStatus.CapacityExceeded"/>, the limit's detail and no regions: pieces and vertices from the
/// document, then the domain, the covered footprint cells, containment, region clipping, bound reads and emitted
/// triangles. A body that fails containment is <see cref="MapWaterCompileStatus.Invalid"/>, and one whose containment
/// is unverified, whose footprints the view cannot prove complete, or whose area a bound leaves unresolved is at most
/// <see cref="MapWaterCompileStatus.Incomplete"/>. A value that cannot narrow from the wide kernel makes the body
/// <see cref="MapWaterCompileStatus.NotRepresentable"/>.</summary>
public static class MapWaterCompiler
{
    /// <summary>The compiled bodies of a deep copy of <paramref name="water"/>, in id order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A compile limit or nested read limit is negative. The parameter
    /// name identifies the limit, with <c>Reads.</c> before a nested read limit's name.</exception>
    public static IReadOnlyList<MapCompiledWaterBody> Compile(MapScopedSurfaces view, MapWaterSet water, MapWaterCompileLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(water);
        limits ??= new MapWaterCompileLimits();
        limits.Validate();
        var admission = view.Scope.SceneAdmission ?? Scene.MapSceneAdmission.Current;
        using var admitted = admission?.Enter();
        using var preparation = admission?.BeginPreparation();
        MapWaterSet snapshot = water.Clone();
        MapWaterConnectivity? graph = null;
        admission?.ReserveBytes(checked(128L + 32L * snapshot.Bodies.Count));
        var bodies = new List<MapCompiledWaterBody>(snapshot.Bodies.Count);
        foreach (MapWaterBodyDoc body in snapshot.Bodies.Where(b => b is not null).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            using var bodyPreparation = admission?.BeginPreparation();
            var compile = new BodyCompile(view, snapshot, body, limits);
            var compiled = compile.Run(ref graph);
            bodyPreparation?.Complete(new MapWaterInventory().Body(compiled));
            bodies.Add(compiled);
        }
        var inventory = new MapWaterInventory();
        preparation?.Complete(checked(128L + 8L * bodies.Capacity + bodies.Sum(inventory.Body)));
        return bodies.AsReadOnly();
    }

    /// <summary>The state of one body's compile, kept so a refused body still reports what it read.</summary>
    internal static MapCompiledWaterBody CompileCell(MapScopedSurfaces view, MapWaterSet water, MapWaterBodyDoc body,
        MapEffectiveWaterBody cell, MapWaterCompileLimits limits)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        MapWaterConnectivity? graph = null;
        var compiled = new BodyCompile(view, water, body, limits, cell).Run(ref graph);
        preparation?.Complete(new MapWaterInventory().Body(compiled));
        return compiled;
    }

    sealed class BodyCompile(MapScopedSurfaces view, MapWaterSet water, MapWaterBodyDoc body, MapWaterCompileLimits limits,
        MapEffectiveWaterBody? cellBody = null)
    {
        readonly MapWaterWorkMeter _meter = new(limits);
        readonly SortedSet<MapPatchKey> _patches = new();
        readonly SortedSet<string> _surfaces = new(StringComparer.Ordinal);
        readonly HashSet<MapRecordRef> _relations = new();
        readonly Dictionary<string, IReadOnlyList<MapWaterFootprintRect>> _footprints = new(StringComparer.Ordinal);
        readonly Dictionary<string, bool> _footprintsComplete = new(StringComparer.Ordinal);
        readonly MapWaterMediumDoc? _medium = water.Media.FirstOrDefault(m => m is not null && m.Key == body.Medium);
        readonly string _label = $"water body '{body.Id}'";
        MapWaterDomain? _domain;
        IReadOnlyList<MapWaterRegion> _regions = Array.Empty<MapWaterRegion>();
        IReadOnlyList<MapWaterClosedContour> _closedContours = Array.Empty<MapWaterClosedContour>();
        MapWideRational? _deepest;
        MapEffectiveWaterBody? _effective;
        bool _usesOccupied;
        IReadOnlyList<MapOccupiedPiece>? _occupied;
        IReadOnlyList<MapWaterFragmentBounds> _fragmentBounds = Array.Empty<MapWaterFragmentBounds>();
        float Level => _effective?.SurfaceY.ToSingle() ?? body.SurfaceY;

        internal MapCompiledWaterBody Run(ref MapWaterConnectivity? graph)
        {
            MapWaterCompileStatus status;
            Scene.MapSceneCapacity? capacity = null;
            string? detail;
            try { (status, detail) = Compile(ref graph); }
            catch (MapWaterCapacityException limit) { (status, detail) = (MapWaterCompileStatus.CapacityExceeded, limit.Message); capacity = limit.Capacity; }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException)
            {
                (status, detail) = (MapWaterCompileStatus.NotRepresentable, $"{_label}: not representable");
            }
            catch (MapDocumentException error)
            {
                (status, detail) = (MapCommonRefinement.NotRepresentable(error) || error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase) ? MapWaterCompileStatus.NotRepresentable
                    : _usesOccupied && error.Message.Contains("missing geometry", StringComparison.Ordinal) ? MapWaterCompileStatus.Incomplete
                    : _usesOccupied && (error.Message.Contains("capacity exceeded", StringComparison.Ordinal) || error.Message.Contains("domain work exceeds", StringComparison.Ordinal))
                        ? MapWaterCompileStatus.CapacityExceeded : MapWaterCompileStatus.Invalid, $"{_label}: {error.Message}");
            }
            if (status is not (MapWaterCompileStatus.Complete or MapWaterCompileStatus.Incomplete))
            {
                _regions = Array.Empty<MapWaterRegion>();
                _closedContours = Array.Empty<MapWaterClosedContour>();
                _deepest = null;
                _fragmentBounds = Array.Empty<MapWaterFragmentBounds>();
            }
            float? deepest = null;
            if (_deepest is { } bed)
            {
                if (_usesOccupied) deepest = (float)bed.ToDouble();
                else if (bed.TryNarrow(out MapExactValue exact)) deepest = exact.ToSingle();
                else (status, detail, _regions) = (MapWaterCompileStatus.NotRepresentable, $"{_label}: deepest bed is not representable",
                    Array.Empty<MapWaterRegion>());
            }
            MapBox3? bounds = _fragmentBounds.Count != 0 ? _fragmentBounds.Select(b => b.Bounds).Aggregate((a, b) => a.Union(b))
                : deepest is { } low ? Bounds(low) : null;
            MapWaterMediumDoc medium = _medium ?? new MapWaterMediumDoc(body.Medium, MapWaterMediumKind.Water, float.NaN);
            string digest = Digest(status, detail, deepest, bounds);
            return new MapCompiledWaterBody(body.Id, Level, medium, body.RenderMaterial, _regions, deepest, bounds, status, detail,
                _meter.Work, digest)
            {
                Capacity = capacity,
                Domain = _domain,
                Footprints = _footprints.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                ClosedContours = _closedContours,
                OccupiedPieces = (_occupied ?? Array.Empty<MapOccupiedPiece>()).Where(p => body.Covers.Any(c => c.Id == p.SpaceId)).ToArray(),
                FragmentFootprints = MapWaterFootprints.ByFragment((_occupied ?? Array.Empty<MapOccupiedPiece>()).Where(p => body.Covers.Any(c => c.Id == p.SpaceId))),
                FragmentBounds = _fragmentBounds,
            };
        }

        (MapWaterCompileStatus, string?) Compile(ref MapWaterConnectivity? graph)
        {
            if (body.Domain is null) return (MapWaterCompileStatus.Invalid, $"{_label}: domain is required");
            _meter.ChargeDomain(body.Domain);
            if (_medium is null) return (MapWaterCompileStatus.Invalid, $"{_label}: unknown medium '{body.Medium}'");
            MapExactValue level;
            try { level = MapExactValue.FromSingle(body.SurfaceY); }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
            {
                return (MapWaterCompileStatus.NotRepresentable, $"{_label}: surfaceY is not representable");
            }
            var errors = new List<string>();
            if (cellBody is not null)
            {
                _effective = cellBody;
                _domain = cellBody.Domain;
                level = cellBody.SurfaceY;
            }
            else if (view.EffectiveContext?.Transform(MapPrefabChildKind.WaterBody, body.Id) is not null)
            {
                _effective = MapEffectiveWater.Resolve(view, body, _meter.Pairs);
                _domain = _effective.Domain;
                level = _effective.SurfaceY;
            }
            else _domain = MapWaterDomain.TryCompile(body.Domain, null, errors, _meter.Pairs);
            if (_domain is not null && view.EffectiveContext is not null)
            {
                _effective ??= new(body, _domain, level, body.Id, body.Covers, "");
                _usesOccupied = MapEffectiveWater.UsesOccupied(view, _effective);
            }
            if (_domain is null)
                return (errors[0].Contains("not representable", StringComparison.Ordinal) ? MapWaterCompileStatus.NotRepresentable
                    : MapWaterCompileStatus.Invalid, $"{_label}: {errors[0]}");

            string[] spaces = (body.Covers ?? Array.Empty<MapRecordRef>()).Where(c => c is not null).Select(c => c.Id).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (!_usesOccupied) foreach (string space in spaces) Footprints(space, out _);
            foreach (MapRecordRef cover in body.Covers ?? Array.Empty<MapRecordRef>())
                if (cover is not null) _patches.Add(cover.Anchor);
            var prepared = new MapWaterContainment.Prepared(level, _domain, Footprints, _meter.Pairs, _patches, _usesOccupied ? Occupied : null,
                _usesOccupied ? _relations : null);
            MapWaterContainmentReport containment = MapWaterContainment.ValidateBody(view, body, prepared, ref graph);
            if (containment.Complete && containment.Errors.Count != 0) return (MapWaterCompileStatus.Invalid, containment.Errors[0]);
            string? incomplete = containment.Errors.FirstOrDefault();
            if (!_usesOccupied && spaces.FirstOrDefault(s => !_footprintsComplete[s]) is { } partial)
                incomplete ??= $"{_label}: footprints of space '{partial}' are not proven complete in this view";

            var builder = new MapWaterRegionBuilder(view, _domain, level, limits.Reads ?? new MapRefinementLimits(), _meter);
            if (_usesOccupied)
            {
                try
                {
                    foreach (string space in spaces)
                        foreach (var piece in Occupied(space)) builder.Add(piece);
                }
                catch (MapDocumentException error) when (error.Message.Contains("missing geometry", StringComparison.Ordinal))
                { return (MapWaterCompileStatus.Incomplete, incomplete ?? $"{_label}: {error.Message}"); }
            }
            else
                foreach (string space in spaces)
                    foreach (MapWaterFootprintRect rect in _footprints[space])
                        if (builder.Add(rect) is { } failure)
                        {
                            Absorb(builder);
                            return (failure.Status, $"{_label}: {failure.Detail}");
                        }
            Absorb(builder);
            _regions = builder.Emit(level);
            _closedContours = MapWaterInventory.ReadOnly(builder.ClosedContours);
            _deepest = builder.DeepestBed;
            _fragmentBounds = builder.FragmentBounds;
            incomplete ??= builder.Unresolved is { } gap ? $"{_label}: {gap}" : null;
            return incomplete is null ? (MapWaterCompileStatus.Complete, null) : (MapWaterCompileStatus.Incomplete, incomplete);
        }

        IReadOnlyList<MapOccupiedPiece> Occupied(string space)
        {
            _occupied ??= MapWaterOccupiedPieces.Of(view, _domain!.Min, _domain.Max, limits.Reads ?? new(), _meter);
            return _occupied.Where(p => p.SpaceId == space).ToArray();
        }

        /// <summary>The footprint rectangles of one covered space near the domain, gathered once under the meter.</summary>
        IReadOnlyList<MapWaterFootprintRect> Footprints(string spaceId, out bool complete)
        {
            if (!_footprints.TryGetValue(spaceId, out IReadOnlyList<MapWaterFootprintRect>? rects))
            {
                rects = MapWaterFootprints.Of(view, spaceId, _domain!.Min, _domain.Max, out bool gathered, out _, out IReadOnlyList<MapPatchKey> read, _meter);
                _meter.CountFootprintRects(rects.Count);
                _patches.UnionWith(read);
                _footprints.Add(spaceId, rects);
                _footprintsComplete.Add(spaceId, gathered);
            }
            complete = _footprintsComplete[spaceId];
            return rects;
        }

        void Absorb(MapWaterRegionBuilder builder)
        {
            _patches.UnionWith(builder.PatchesRead);
            _surfaces.UnionWith(builder.SurfacesRead);
        }

        MapBox3? Bounds(float deepest)
        {
            var points = _regions.SelectMany(r => r.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
            if (points.Length == 0) return null;
            double low = CanonicalZero(Math.Min(deepest, body.SurfaceY)), high = CanonicalZero(Math.Max(deepest, body.SurfaceY));
            return new MapBox3(points.Min(p => p.X.ToDouble()), low, points.Min(p => p.Z.ToDouble()),
                points.Max(p => p.X.ToDouble()), high, points.Max(p => p.Z.ToDouble()));
        }

        /// <summary>The canonical SHA-256 of what the body is and exactly what it read: its definition and medium, the
        /// metadata of every surface whose patches or bounds it read, the semantic digest of every patch it read, its
        /// outcome and its regions with exact coordinates.</summary>
        string Digest(MapWaterCompileStatus status, string? detail, float? deepest, MapBox3? bounds)
        {
            var present = view.Witness.Present.ToDictionary(p => p.Key, p => p.Value);
            var patches = _patches.Where(present.ContainsKey).ToArray();
            var surfaces = new SortedSet<string>(_surfaces, StringComparer.Ordinal);
            surfaces.UnionWith(patches.Select(p => p.SurfaceId));
            return MapWaterJson.Hash("kemap/water-body/1", w =>
            {
                w.WriteStartObject();
                w.WriteString("body", MapWaterSet.BodyDigest(body));
                w.WriteString("medium", _medium is null ? null : MapWaterSet.MediumDigest(_medium));
                if (_usesOccupied)
                {
                    w.WriteString("transform", _effective!.TransformDigest);
                    w.WriteNumber("occupiedPolicy", 2);
                    w.WriteString("deepestExact", _deepest?.ToString());
                    w.WriteStartArray("relations");
                    foreach (var reference in _relations.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Anchor))
                    {
                        if (!view.TryRecord(reference, out var record, out _, borrow: true)) continue;
                        var ids = record switch
                        {
                            MapBoundaryChain chain => chain.Vertices.Select(v => v.Vertex.SurfaceId).Concat(chain.SourcePatch is { } patch ? new[] { patch.SurfaceId } : []),
                            MapHorizontalOpening opening => new[] { opening.Patch.SurfaceId },
                            MapCavePortal portal => portal.Interval.Select(v => v.SurfaceId),
                            _ => Array.Empty<string>(),
                        };
                        var frames = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
                        {
                            var surface = MapSpaceGeometry.Surface(view, id);
                            return new
                            {
                                surface.Id,
                                surface.Frame,
                                surface.Role,
                                surface.PresencePolicy,
                                Pose = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, id)?.Transform
                            };
                        }).ToArray();
                        w.WriteStringValue(MapPrefabJson.Digest("kemap/water-relation/1", new
                        { Record = record, Pose = view.EffectiveContext?.Transform(MapPrefabChildKind.Record, reference.Id)?.Transform, Frames = frames }));
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("fragments");
                    foreach (var piece in (_occupied ?? Array.Empty<MapOccupiedPiece>()).Where(p => body.Covers.Any(c => c.Id == p.SpaceId))
                        .OrderBy(p => p.FragmentId, StringComparer.Ordinal).ThenBy(p => p.Polygon[0]))
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(piece.FragmentId);
                        foreach (var boundary in new[] { piece.LowerBoundary, piece.UpperBoundary })
                        {
                            w.WriteStringValue(boundary.OwnerId); w.WriteStringValue(boundary.Role); w.WriteStringValue(boundary.SemanticDigest);
                        }
                        // Ancestor bounds certify the intersection even where a prism plane wins. Logical path
                        // identities omit semantics, so consumed ancestor metadata enters the body hash here.
                        for (int ancestor = 0; ancestor + 4 < piece.Path.Count; ancestor += 5)
                        {
                            w.WriteStringValue(MapOccupiedPartitions.Boundary(view, piece.Path[ancestor], "ancestor-space").SemanticDigest);
                            w.WriteStringValue(MapOccupiedPartitions.Boundary(view, piece.Path[ancestor + 1], piece.Path[ancestor + 2]).SemanticDigest);
                            w.WriteStringValue(MapOccupiedPartitions.Boundary(view, piece.Path[ancestor + 3], piece.Path[ancestor + 4]).SemanticDigest);
                        }
                        foreach (var point in piece.Polygon)
                        {
                            w.WriteStringValue(point.X.ToString()); w.WriteStringValue(point.Z.ToString());
                            w.WriteStringValue(piece.Bottom(point).ToString()); w.WriteStringValue(piece.Top(point)?.ToString());
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteStartArray("surfaces");
                foreach (MapSurfaceRef surface in surfaces.Select(id => view.Surfaces.FirstOrDefault(s => s.Id == id)).OfType<MapSurfaceRef>())
                {
                    w.WriteStartObject();
                    w.WriteString("id", surface.Id);
                    w.WritePropertyName("frame");
                    MapWaterJson.WriteFrame(w, surface.Frame);
                    w.WriteString("role", surface.Role.ToString());
                    w.WriteString("presencePolicy", surface.PresencePolicy.ToString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("patches");
                foreach (MapPatchKey key in patches)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(key.ToString());
                    w.WriteStringValue(present[key]);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteString("status", status.ToString());
                w.WriteString("detail", detail);
                if (deepest is { } bed) w.WriteNumber("deepestBedBits", BitConverter.SingleToInt32Bits(bed == 0f ? 0f : bed));
                else w.WriteNull("deepestBedBits");
                if (bounds is { } box)
                {
                    w.WriteStartArray("bounds");
                    foreach (double value in new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ })
                        w.WriteStringValue(BitConverter.DoubleToInt64Bits(CanonicalZero(value)).ToString(CultureInfo.InvariantCulture));
                    w.WriteEndArray();
                }
                else w.WriteNull("bounds");
                w.WriteStartArray("regions");
                foreach (MapWaterRegion region in _regions)
                {
                    w.WriteStartObject();
                    w.WriteString("space", region.SpaceId);
                    w.WriteString("kind", region.Kind.ToString());
                    if (region.Fragment is { } fragment) w.WriteString("fragment", fragment.Id);
                    w.WriteStartArray("triangles");
                    if (region.WidePolygons.Count != 0)
                    {
                        foreach (var polygon in region.WidePolygons)
                            foreach (var point in polygon)
                            { w.WriteStringValue(point.X.ToString()); w.WriteStringValue(_effective!.SurfaceY.ToString()); w.WriteStringValue(point.Z.ToString()); }
                    }
                    else
                        foreach (MapExactTriangle t in region.Triangles)
                            foreach (MapExactPoint p in new[] { t.A, t.B, t.C })
                            {
                                w.WriteStringValue(p.X.ToString());
                                w.WriteStringValue(p.Y.ToString());
                                w.WriteStringValue(p.Z.ToString());
                            }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("closedContours");
                foreach (MapWaterClosedContour contour in _closedContours)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(contour.SpaceId);
                    if (contour.FragmentId is { } fragment) w.WriteStringValue(fragment);
                    var a = contour.WideA ?? MapWideXz.From(contour.A); var b = contour.WideB ?? MapWideXz.From(contour.B);
                    w.WriteStringValue(a.X.ToString()); w.WriteStringValue(a.Z.ToString());
                    w.WriteStringValue(b.X.ToString()); w.WriteStringValue(b.Z.ToString());
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        static double CanonicalZero(double value) => value == 0d ? 0d : value;
    }
}
