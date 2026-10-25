using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Exact predicates over emitted region triangles.</summary>
internal static class MapWaterRegionGeometry
{
    internal static bool Holds(MapWaterRegion region, MapWideXz point) => region.WidePolygons.Count == 0
        ? region.Triangles.Any(t => TriangleHolds(t, point)) : region.WidePolygons.Any(p =>
            Enumerable.Range(0, p.Count).All(i => MapWaterPolygons.Cross(p[i], p[(i + 1) % p.Count], point).Sign >= 0));

    /// <summary>A drawing-only bounded adapter. Semantic predicates and hashes never consume rounded vertices.</summary>
    internal static MapExactXz Drawing(MapWideXz point) => point.TryNarrow(out var exact) ? exact
        : new(MapResourceTransform.Freeze(point.X.ToDouble()), MapResourceTransform.Freeze(point.Z.ToDouble()));

    /// <summary>True when the triangle holds <paramref name="point"/> in XZ, boundaries included.</summary>
    internal static bool TriangleHolds(MapExactTriangle triangle, MapWideXz point)
    {
        MapWideXz a = Xz(triangle.A), b = Xz(triangle.B), c = Xz(triangle.C);
        int s1 = MapWaterPolygons.Cross(a, b, point).Sign, s2 = MapWaterPolygons.Cross(b, c, point).Sign, s3 = MapWaterPolygons.Cross(c, a, point).Sign;
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
    }

    static MapWideXz Xz(MapExactPoint point) => new(MapWideRational.From(point.X), MapWideRational.From(point.Z));
}

/// <summary>Builds one body's regions from its footprint rectangles. For each rectangle it reads the lower bound and,
/// unless the top is open, the upper bound over the part of the rectangle the domain's bounds meet, then clips each
/// convex domain part to the rectangle and sweeps the result cell by cell over each bound's own lattice. A cell wholly
/// inside the part keeps its faces whole, a cell on the part's edge clips them. The upper faces, lifted onto their
/// planes, split at the body's level: the part strictly above is free surface and the rest is closed. The exact area
/// the faces and compatibility tags cover must equal each cell's share of the part, and any shortfall, from a missing
/// cell, a known-empty patch or an unread chunk, becomes unresolved area that the free and closed parts give up. The
/// lower bound never clips the surface. It only proves coverage and finds the deepest bed.</summary>
internal sealed class MapWaterRegionBuilder
{
    readonly MapScopedSurfaces _view;
    readonly MapWaterDomain _domain;
    readonly MapWideRational _level;
    readonly MapRefinementLimits _reads;
    readonly MapWaterWorkMeter _meter;
    readonly MapWaterRegionStorage _storage = new();
    readonly SortedDictionary<string, SpaceRegions> _spaces = new(StringComparer.Ordinal);
    readonly Dictionary<string, MapOccupiedFragment> _fragments = new(StringComparer.Ordinal);
    readonly Dictionary<string, MapWaterFragmentBounds> _bounds = new(StringComparer.Ordinal);
    MapWideRational? _deepest;

    internal MapWaterRegionBuilder(MapScopedSurfaces view, MapWaterDomain domain, MapExactValue level, MapRefinementLimits reads,
        MapWaterWorkMeter meter)
    {
        _view = view;
        _domain = domain;
        _level = MapWideRational.From(level);
        _reads = reads;
        _meter = meter;
    }

    /// <summary>Every patch a bound read consumed.</summary>
    internal SortedSet<MapPatchKey> PatchesRead { get; } = new();

    /// <summary>Every surface a bound read looked up.</summary>
    internal SortedSet<string> SurfacesRead { get; } = new(StringComparer.Ordinal);

    internal List<MapWaterClosedContour> ClosedContours { get; } = new();

    /// <summary>Why the first unresolved area could not be resolved, or null while every area is resolved.</summary>
    internal string? Unresolved { get; private set; }

    /// <summary>The exact minimum height of the covered lower area, or null when none resolved.</summary>
    internal MapWideRational? DeepestBed => _deepest;
    internal IReadOnlyList<MapWaterFragmentBounds> FragmentBounds => _bounds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();

    /// <summary>R5 feeds the existing clipping and height-split kernel canonical occupied pieces, rather than raw
    /// ancestor footprints. Each piece is already refined against its canonical bound faces and parent prism.</summary>
    internal void Add(MapOccupiedPiece occupied)
    {
        var fragment = MapOccupiedPartitions.Fragment(occupied.SpaceId, occupied.LowerBoundary, occupied.UpperBoundary, occupied.Path);
        _fragments.TryAdd(fragment.Id, fragment);
        var regions = Regions(fragment.Id);
        for (int index = 0; index < _domain.Pieces.Count; index++)
            foreach (var part in _domain.ConvexParts(index, _meter.Pairs))
            {
                _meter.Pairs.Charge(1);
                foreach (var clipped in MapWaterPolygons.ClipToConvex(part, occupied.Polygon))
                {
                    var wet = MapOccupiedPartitions.Clip(clipped, p => _level.Subtract(occupied.Bottom(p)));
                    if (!MapOccupiedPartitions.Positive(wet) || !wet.Any(p => occupied.Bottom(p).CompareTo(_level) < 0)) continue;
                    var low = wet.Select(occupied.Bottom).Aggregate(MapWideRational.Min);
                    _deepest = _deepest is { } previous ? MapWideRational.Min(previous, low) : low;
                    var high = wet.Select(p => MapWideRational.Min(_level, occupied.Top(p) ?? _level)).Aggregate(MapWideRational.Max);
                    var bounds = MapWaterOccupiedPieces.Bounds(wet, low, high);
                    _bounds[fragment.Id] = new(fragment, _bounds.TryGetValue(fragment.Id, out var old) ? old.Bounds.Union(bounds) : bounds);
                    if (occupied.Top(wet[0]) is null) _storage.Polygons(regions[MapWaterRegionKind.FreeSurface], new[] { wet });
                    else
                    {
                        _meter.Pairs.Charge(1);
                        var lifted = wet.Select(p => occupied.Lift(p, true)).ToArray();
                        var (free, closed) = MapWaterPolygons.SplitAtHeight(lifted, _level);
                        _storage.Polygons(regions[MapWaterRegionKind.FreeSurface], free.Select(Flatten));
                        _storage.Polygons(regions[MapWaterRegionKind.Closed], closed.Select(Flatten));
                        if (closed.Count == 0)
                        {
                            var ties = lifted.Where(p => p.Y.CompareTo(_level) == 0).Select(p => p.Xz).Order().ToArray();
                            if (ties.Length != 0) _storage.Append(ClosedContours, new(occupied.SpaceId, fragment.Id, ties[0], ties[^1]));
                        }
                    }
                }
            }
    }

    /// <summary>Adds one footprint rectangle's regions, or returns the typed failure of a bound read.</summary>
    internal (MapWaterCompileStatus Status, string? Detail)? Add(MapWaterFootprintRect rect)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        MapWideXz min = new(MapWideRational.Max(Wide(rect.Min.X), Wide(_domain.Min.X)), MapWideRational.Max(Wide(rect.Min.Z), Wide(_domain.Min.Z)));
        MapWideXz max = new(MapWideRational.Min(Wide(rect.Max.X), Wide(_domain.Max.X)), MapWideRational.Min(Wide(rect.Max.Z), Wide(_domain.Max.Z)));
        if (min.X.CompareTo(max.X) >= 0 || min.Z.CompareTo(max.Z) >= 0) return null;
        MapExactXz windowMin = Narrow(min), windowMax = Narrow(max);
        MapWaterBoundRead lower = Read(rect, rect.Lower, windowMin, windowMax, upper: false);
        if (!Usable(lower)) return Failure(lower);
        MapWaterBoundRead? upper = null;
        if (rect.Upper.Kind != MapBoundKind.OpenTop)
        {
            upper = Read(rect, rect.Upper, windowMin, windowMax, upper: true);
            if (!Usable(upper)) return Failure(upper);
        }
        // Read outputs, indexes and clipping scratch end here. Appended polygons and contours
        // transfer their share of this envelope into the body collector before this scope closes.
        preparation?.Reserve(checked(512L + 1280L * (lower.Faces.Count + (upper?.Faces.Count ?? 0))));
        Cells lowerCells = Index(lower), upperCells = upper is null ? Cells.Open : Index(upper);
        MapWideXz rectMin = MapWideXz.From(rect.Min), rectMax = MapWideXz.From(rect.Max);
        for (int piece = 0; piece < _domain.Pieces.Count; piece++)
            foreach (IReadOnlyList<MapWideXz> part in _domain.ConvexParts(piece, _meter.Pairs))
            {
                _meter.Pairs.Charge(1);
                IReadOnlyList<MapWideXz> clipped = MapWaterPolygons.ClipToRect(part, rectMin, rectMax);
                if (clipped.Count == 0) continue;
                Compose(rect.SpaceId, clipped, lowerCells, upperCells, lower.Gap ?? upper?.Gap);
            }
        return null;
    }

    /// <summary>The regions of every space in ordinal order, each kind in enum order, as up-facing triangles at the
    /// body's level. Throws <see cref="MapExactOverflowException"/> when a vertex cannot narrow.</summary>
    internal IReadOnlyList<MapWaterRegion> Emit(MapExactValue level)
    {
        var regions = new List<MapWaterRegion>();
        foreach (var (space, polygons) in _spaces)
            foreach (MapWaterRegionKind kind in Enum.GetValues<MapWaterRegionKind>())
            {
                var triangles = new List<MapExactTriangle>();
                foreach (IReadOnlyList<MapWideXz> polygon in polygons[kind])
                {
                    List<MapWideXz> ring = Clean(polygon);
                    if (ring.Count < 3) continue;
                    _meter.ChargeTriangles(ring.Count - 2);
                    bool effective = _fragments.ContainsKey(space);
                    MapExactPoint Draw(MapWideXz p) => effective ? new(MapWaterRegionGeometry.Drawing(p).X, level, MapWaterRegionGeometry.Drawing(p).Z) : Lift(p, level);
                    MapExactPoint first = Draw(ring[0]);
                    for (int i = 1; i + 1 < ring.Count; i++)
                        // The ring is counter-clockwise in XZ, so reversing each fan triangle makes it face up.
                        triangles.Add(new MapExactTriangle(first, Draw(ring[i + 1]), Draw(ring[i])));
                }
                if (triangles.Count != 0)
                {
                    _fragments.TryGetValue(space, out var fragment);
                    regions.Add(new MapWaterRegion(fragment?.SpaceId ?? space, kind, triangles.AsReadOnly())
                    { Fragment = fragment, WidePolygons = fragment is null ? Array.Empty<IReadOnlyList<MapWideXz>>() : polygons[kind].Select(p => (IReadOnlyList<MapWideXz>)Clean(p).AsReadOnly()).ToArray() });
                }
            }
        return regions.AsReadOnly();
    }

    MapWaterBoundRead Read(MapWaterFootprintRect rect, MapBoundRef bound, MapExactXz min, MapExactXz max, bool upper)
    {
        MapWaterBoundRead read = MapWaterBoundReads.Read(_view, rect.Footprint, bound, min, max, _reads, _meter, upper);
        if (read.SurfaceId is { } surface) SurfacesRead.Add(surface);
        PatchesRead.UnionWith(read.PatchesRead);
        return read;
    }

    /// <summary>A complete read, or one whose missing geometry only leaves its area unresolved.</summary>
    static bool Usable(MapWaterBoundRead read) => read.Status is MapRefinementStatus.Complete or MapRefinementStatus.MissingGeometry;

    static (MapWaterCompileStatus, string?) Failure(MapWaterBoundRead read) => (read.Status switch
    {
        MapRefinementStatus.CapacityExceeded => MapWaterCompileStatus.CapacityExceeded,
        MapRefinementStatus.NotRepresentable => MapWaterCompileStatus.NotRepresentable,
        _ => MapWaterCompileStatus.Invalid,
    }, read.Detail);

    /// <summary>Adds one convex part of the domain within one footprint rectangle.</summary>
    void Compose(string spaceId, IReadOnlyList<MapWideXz> part, Cells lower, Cells upper, string? gap)
    {
        var lowerGaps = new List<IReadOnlyList<MapWideXz>>();
        MapWideRational? bed = null;
        Sweep(part, lower, lowerGaps, (face, piece, whole) =>
        {
            MapWideRational low = whole ? face.MinY : piece.Select(face.Lift).Aggregate(MapWideRational.Min);
            bed = bed is { } b ? MapWideRational.Min(b, low) : low;
        }, legacy => bed = bed is { } b ? MapWideRational.Min(b, legacy) : legacy);
        if (bed is { } deepest) _deepest = _deepest is { } d ? MapWideRational.Min(d, deepest) : deepest;

        var free = new List<IReadOnlyList<MapWideXz>>();
        var closed = new List<IReadOnlyList<MapWideXz>>();
        var upperGaps = new List<IReadOnlyList<MapWideXz>>();
        if (ReferenceEquals(upper, Cells.Open)) free.Add(part);
        else
            Sweep(part, upper, upperGaps, (face, piece, whole) =>
            {
                IReadOnlyList<MapWidePoint> lifted = whole ? face.Points : piece.Select(v => new MapWidePoint(v.X, face.Lift(v), v.Z)).ToArray();
                var (above, rest) = MapWaterPolygons.SplitAtHeight(lifted, _level);
                if (rest.Count == 0)
                {
                    MapWideXz[] ties = lifted.Where(p => p.Y.CompareTo(_level) == 0).Select(p => p.Xz)
                        .OrderBy(p => p.X).ThenBy(p => p.Z).ToArray();
                    if (ties.Length != 0) _storage.Append(ClosedContours, new(spaceId, Narrow(ties[0]), Narrow(ties[^1])));
                }
                free.AddRange(above.Select(Flatten));
                closed.AddRange(rest.Select(Flatten));
            }, _ => { });

        // Area both bounds left unread counts once, as the upper bound's.
        IReadOnlyList<IReadOnlyList<MapWideXz>> lowerOnly = Without(lowerGaps, upperGaps);
        SpaceRegions regions = Regions(spaceId);
        _storage.Polygons(regions[MapWaterRegionKind.FreeSurface], Without(free, lowerOnly));
        _storage.Polygons(regions[MapWaterRegionKind.Closed], Without(closed, lowerOnly));
        _storage.Polygons(regions[MapWaterRegionKind.Unresolved], upperGaps);
        _storage.Polygons(regions[MapWaterRegionKind.Unresolved], lowerOnly);
        if (upperGaps.Count + lowerOnly.Count != 0) Unresolved ??= gap ?? $"unresolved bound area in space '{spaceId}'";
    }

    /// <summary>Sweeps the convex <paramref name="part"/> over the bound's lattice one row at a time. Each face of a
    /// cell the part meets reaches <paramref name="onFace"/> whole when the cell lies inside the part, else clipped to
    /// the part. A compatibility cell reaches <paramref name="onLegacy"/> and counts as covered. The part of each cell
    /// the faces leave uncovered joins <paramref name="gaps"/>, runs of wholly empty inside cells as one rectangle.</summary>
    void Sweep(IReadOnlyList<MapWideXz> part, Cells cells, List<IReadOnlyList<MapWideXz>> gaps,
        Action<Face, IReadOnlyList<MapWideXz>, bool> onFace, Action<MapWideRational> onLegacy)
    {
        if (cells.Frame is null)
        {
            gaps.Add(part);
            return;
        }
        MapWaterPolygons.Bounds bounds = MapWaterPolygons.Bounds.Of(part);
        var (row0, row1) = cells.Rows(bounds.Min.Z, bounds.Max.Z);
        var (firstCol, endCol) = cells.Columns(bounds.Min.X, bounds.Max.X);
        _meter.ChargeBoundTraversal(BigInteger.Zero, ((BigInteger)row1 - row0) * ((BigInteger)endCol - firstCol));
        for (long row = row0; row < row1; row++)
        {
            var (zLo, zHi) = cells.RowBand(row);
            IReadOnlyList<MapWideXz> band = MapWaterPolygons.ClipToRect(part, new(bounds.Min.X, zLo), new(bounds.Max.X, zHi));
            if (band.Count == 0) continue;
            (MapWideRational Min, MapWideRational Max)? inner = Inner(MapWaterPolygons.ChordAt(part, zLo), MapWaterPolygons.ChordAt(part, zHi));
            MapWaterPolygons.Bounds extent = MapWaterPolygons.Bounds.Of(band);
            var (col0, col1) = cells.Columns(extent.Min.X, extent.Max.X);
            long? run = null;
            for (long col = col0; col < col1; col++)
            {
                MapWideRational xLo = cells.Edge(col), xHi = cells.Edge(col + 1);
                bool inside = inner is { } chord && chord.Min.CompareTo(xLo) <= 0 && xHi.CompareTo(chord.Max) <= 0;
                cells.Map.TryGetValue((col, row), out Cell? cell);
                if (inside && (cell is null || (cell.Faces.Count == 0 && cell.Legacy is null)))
                {
                    run ??= col;
                    continue;
                }
                Flush(col);
                IReadOnlyList<MapWideXz> share = inside ? Rect(xLo, zLo, xHi, zHi) : MapWaterPolygons.ClipToRect(band, new(xLo, zLo), new(xHi, zHi));
                if (share.Count == 0) continue;
                if (cell?.Legacy is { } legacy)
                {
                    onLegacy(legacy);
                    continue;
                }
                MapWideRational covered = default;
                foreach (Face face in cell?.Faces ?? new List<Face>())
                {
                    _meter.Pairs.Charge(1);
                    if (inside && face.Within(xLo, zLo, xHi, zHi))
                    {
                        covered = covered.Add(face.Area);
                        onFace(face, face.Xz, true);
                        continue;
                    }
                    foreach (IReadOnlyList<MapWideXz> piece in MapWaterPolygons.ClipToConvex(share, face.Xz))
                    {
                        covered = covered.Add(MapWaterPolygons.SignedArea(piece));
                        onFace(face, piece, false);
                    }
                }
                if (covered.CompareTo(MapWaterPolygons.SignedArea(share)) < 0)
                    gaps.AddRange(Without(new[] { share }, (cell?.Faces ?? new List<Face>()).Select(f => f.Xz).ToArray()));
            }
            Flush(col1);

            void Flush(long end)
            {
                if (run is not { } start) return;
                gaps.Add(Rect(cells.Edge(start), zLo, cells.Edge(end), zHi));
                run = null;
            }
        }
    }

    /// <summary>The convex <paramref name="polygons"/> less every one of <paramref name="holes"/>, each subtraction of a
    /// hole from a polygon charged as one pair check.</summary>
    IReadOnlyList<IReadOnlyList<MapWideXz>> Without(IReadOnlyList<IReadOnlyList<MapWideXz>> polygons, IReadOnlyList<IReadOnlyList<MapWideXz>> holes)
    {
        IReadOnlyList<IReadOnlyList<MapWideXz>> result = polygons;
        foreach (IReadOnlyList<MapWideXz> hole in holes)
            result = result.SelectMany(polygon =>
            {
                _meter.Pairs.Charge(1);
                return MapWaterPolygons.Subtract(polygon, hole);
            }).ToArray();
        return result;
    }

    SpaceRegions Regions(string spaceId)
    {
        if (!_spaces.TryGetValue(spaceId, out SpaceRegions? regions))
        {
            _storage.Reserve(256);
            _spaces.Add(spaceId, regions = new SpaceRegions());
        }
        return regions;
    }

    /// <summary>Indexes a read's faces by the lattice cell that holds each face's centroid, and its compatibility tags
    /// by their cell with the lowest of the cell's legacy corner heights.</summary>
    Cells Index(MapWaterBoundRead read)
    {
        if (read.Frame is null) return new Cells(null);
        var cells = new Cells(read.Frame);
        foreach (MapBoundFace bound in read.Faces)
        {
            Face? face = Face.Of(bound.Triangle);
            if (face is null) continue;
            MapWideRational three = MapWideRational.From(3);
            var centroid = new MapWideXz(face.Xz[0].X.Add(face.Xz[1].X).Add(face.Xz[2].X).Divide(three),
                face.Xz[0].Z.Add(face.Xz[1].Z).Add(face.Xz[2].Z).Divide(three));
            cells.At(cells.CellOf(centroid)).Faces.Add(face);
        }
        foreach (MapLegacyCellTag tag in read.Compatibility)
        {
            if (!_view.TryAcquiredPatch(tag.Patch, out MapSurfacePatch? patch, out _) || patch is null) continue;
            int x = tag.SlotCell % MapPatchKey.SlotCells - patch.CellMinX, z = tag.SlotCell / MapPatchKey.SlotCells - patch.CellMinZ;
            int lowest = Math.Min(Math.Min(patch.Height(x, z), patch.Height(x + 1, z)), Math.Min(patch.Height(x, z + 1), patch.Height(x + 1, z + 1)));
            long cellX = checked(tag.Patch.SlotX * MapPatchKey.SlotCells + tag.SlotCell % MapPatchKey.SlotCells);
            long cellZ = checked(tag.Patch.SlotZ * MapPatchKey.SlotCells + tag.SlotCell / MapPatchKey.SlotCells);
            cells.At((cellX, cellZ)).Legacy = MapWideRational.From(read.Frame.Metres(new MapExactValue(lowest, 1)));
        }
        return cells;
    }

    static (MapWideRational Min, MapWideRational Max)? Inner((MapWideRational Min, MapWideRational Max)? a, (MapWideRational Min, MapWideRational Max)? b)
    {
        if (a is not { } first || b is not { } second) return null;
        MapWideRational min = MapWideRational.Max(first.Min, second.Min), max = MapWideRational.Min(first.Max, second.Max);
        return min.CompareTo(max) < 0 ? (min, max) : null;
    }

    static IReadOnlyList<MapWideXz> Rect(MapWideRational x0, MapWideRational z0, MapWideRational x1, MapWideRational z1) =>
        new MapWideXz[] { new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1) };

    static IReadOnlyList<MapWideXz> Flatten(IReadOnlyList<MapWidePoint> polygon) => polygon.Select(p => p.Xz).ToArray();

    /// <summary>The ring without repeated or straight vertices, so its fan has no zero-area triangle.</summary>
    static List<MapWideXz> Clean(IReadOnlyList<MapWideXz> polygon)
    {
        var ring = new List<MapWideXz>(polygon);
        for (bool changed = true; changed && ring.Count >= 3;)
        {
            changed = false;
            for (int i = 0; i < ring.Count && ring.Count >= 3; i++)
                if (MapWaterPolygons.Cross(ring[(i + ring.Count - 1) % ring.Count], ring[i], ring[(i + 1) % ring.Count]).Sign == 0)
                {
                    ring.RemoveAt(i--);
                    changed = true;
                }
        }
        return ring;
    }

    static MapExactPoint Lift(MapWideXz point, MapExactValue level)
    {
        MapExactXz xz = Narrow(point);
        return new MapExactPoint(xz.X, level, xz.Z);
    }

    static MapExactXz Narrow(MapWideXz point) => point.TryNarrow(out MapExactXz exact) ? exact : throw new MapExactOverflowException();

    static MapWideRational Wide(MapExactValue value) => MapWideRational.From(value);

    sealed class SpaceRegions
    {
        readonly List<IReadOnlyList<MapWideXz>>[] _kinds = { new(), new(), new() };
        internal List<IReadOnlyList<MapWideXz>> this[MapWaterRegionKind kind] => _kinds[(int)kind];
    }

    /// <summary>The faces of one bound read by lattice cell. A null <see cref="Frame"/> means the bound's surface was
    /// unavailable, so nothing covers the part. <see cref="Open"/> stands for an open top.</summary>
    sealed class Cells
    {
        readonly MapWideRational _unit;
        readonly bool _negativeZ;

        internal Cells(MapLatticeFrame? frame)
        {
            Frame = frame;
            if (frame is null) return;
            _unit = MapWideRational.From(frame.CellUnitMetres.Exact());
            _negativeZ = frame.RowDirection == MapRowDirection.NegativeZ;
        }

        internal static Cells Open { get; } = new(null);
        internal MapLatticeFrame? Frame { get; }
        internal Dictionary<(long X, long Z), Cell> Map { get; } = new();

        internal Cell At((long X, long Z) key)
        {
            if (!Map.TryGetValue(key, out Cell? cell)) Map.Add(key, cell = new Cell());
            return cell;
        }

        internal (long X, long Z) CellOf(MapWideXz point) =>
            (Floor(point.X.Divide(_unit)), Floor((_negativeZ ? point.Z.Negate() : point.Z).Divide(_unit)));

        /// <summary>The lattice rows whose band meets world Z from <paramref name="minZ"/> to <paramref name="maxZ"/>,
        /// end exclusive.</summary>
        internal (long First, long End) Rows(MapWideRational minZ, MapWideRational maxZ) => _negativeZ
            ? (Floor(maxZ.Negate().Divide(_unit)), Ceiling(minZ.Negate().Divide(_unit)))
            : (Floor(minZ.Divide(_unit)), Ceiling(maxZ.Divide(_unit)));

        internal (long First, long End) Columns(MapWideRational minX, MapWideRational maxX) =>
            (Floor(minX.Divide(_unit)), Ceiling(maxX.Divide(_unit)));

        /// <summary>The world Z band of a lattice row, low end first.</summary>
        internal (MapWideRational Low, MapWideRational High) RowBand(long row)
        {
            MapWideRational a = Edge(row), b = Edge(row + 1);
            return _negativeZ ? (b.Negate(), a.Negate()) : (a, b);
        }

        internal MapWideRational Edge(long index) => MapWideRational.From(index).Multiply(_unit);

        static long Floor(MapWideRational value) => (long)BigInteger.Divide(
            value.Numerator - (value.Numerator.Sign < 0 ? value.Denominator - BigInteger.One : BigInteger.Zero), value.Denominator);

        static long Ceiling(MapWideRational value) => -Floor(value.Negate());
    }

    sealed class Cell
    {
        internal List<Face> Faces { get; } = new();
        internal MapWideRational? Legacy { get; set; }
    }

    /// <summary>One bound face counter-clockwise in XZ, with its plane for lifting XZ points onto it.</summary>
    sealed class Face
    {
        readonly MapWideRational _nx, _ny, _nz;

        Face(MapWidePoint a, MapWidePoint b, MapWidePoint c, MapWideRational twiceArea)
        {
            Points = new[] { a, b, c };
            Xz = new[] { a.Xz, b.Xz, c.Xz };
            Area = twiceArea.Divide(MapWideRational.From(2));
            MinY = MapWideRational.Min(a.Y, MapWideRational.Min(b.Y, c.Y));
            MapWideRational ux = b.X.Subtract(a.X), uy = b.Y.Subtract(a.Y), uz = b.Z.Subtract(a.Z);
            MapWideRational vx = c.X.Subtract(a.X), vy = c.Y.Subtract(a.Y), vz = c.Z.Subtract(a.Z);
            _nx = uy.Multiply(vz).Subtract(uz.Multiply(vy));
            _ny = uz.Multiply(vx).Subtract(ux.Multiply(vz));
            _nz = ux.Multiply(vy).Subtract(uy.Multiply(vx));
        }

        internal MapWidePoint[] Points { get; }
        internal MapWideXz[] Xz { get; }
        internal MapWideRational Area { get; }
        internal MapWideRational MinY { get; }

        /// <summary>The face counter-clockwise in XZ, or null when its XZ projection has no area.</summary>
        internal static Face? Of(MapExactTriangle triangle)
        {
            MapWidePoint a = Wide(triangle.A), b = Wide(triangle.B), c = Wide(triangle.C);
            MapWideRational twice = MapWaterPolygons.Cross(a.Xz, b.Xz, c.Xz);
            if (twice.Sign == 0) return null;
            return twice.Sign > 0 ? new Face(a, b, c, twice) : new Face(a, c, b, twice.Negate());
        }

        /// <summary>The height of the face's plane at <paramref name="point"/>.</summary>
        internal MapWideRational Lift(MapWideXz point)
        {
            MapWidePoint a = Points[0];
            return a.Y.Subtract(_nx.Multiply(point.X.Subtract(a.X)).Add(_nz.Multiply(point.Z.Subtract(a.Z))).Divide(_ny));
        }

        internal bool Within(MapWideRational x0, MapWideRational z0, MapWideRational x1, MapWideRational z1) =>
            Xz.All(p => x0.CompareTo(p.X) <= 0 && p.X.CompareTo(x1) <= 0 && z0.CompareTo(p.Z) <= 0 && p.Z.CompareTo(z1) <= 0);

        static MapWidePoint Wide(MapExactPoint point) =>
            new(MapWideRational.From(point.X), MapWideRational.From(point.Y), MapWideRational.From(point.Z));
    }
}
