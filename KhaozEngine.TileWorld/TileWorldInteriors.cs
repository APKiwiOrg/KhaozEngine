using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>Safe interior rectangles and sampled roofs from a detached tile-world document.</summary>
public sealed class TileWorldInteriors : IInteriorSource
{
    readonly TileWorldDocument _document;
    readonly TileWorldCatalogs _catalogs;
    readonly ITileMeshGeometry _meshes;
    readonly TileInteriorRoofSamples _samples;
    long? _encodingDigest;
    (float Floor, float Top) _encodingBounds;
    InteriorEncoding _encoding;

    public TileWorldInteriors(TileWorldDocument detached, TileWorldCatalogs catalogs, ITileMeshGeometry meshes)
        : this(detached, catalogs, meshes, new TileInteriorRoofSamples()) { }

    internal TileWorldInteriors(TileWorldDocument detached, TileWorldCatalogs catalogs, ITileMeshGeometry meshes,
        TileInteriorRoofSamples samples)
    {
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(samples);
        _document = detached;
        _catalogs = catalogs;
        _meshes = meshes;
        _samples = samples;
    }

    public IReadOnlyList<TileWorldIssue> LastIssues { get; private set; } = Array.Empty<TileWorldIssue>();

    /// <summary>The largest cell no wider than 25 cm that divides an authored tile pitch.</summary>
    public static float CellSizeFor(float tileSize)
    {
        if (!float.IsFinite(tileSize) || tileSize <= 0f) throw new ArgumentOutOfRangeException(nameof(tileSize));
        // Preserve the authored decimal pitch, so 1.2 / 5 rounds to 0.24 rather than the next float.
        double size = double.Parse(tileSize.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return (float)(size / Math.Ceiling(size / 0.25d));
    }

    public InteriorSourceResult Collect(InteriorWindow window)
    {
        // Validate through the public window contract without allocating a field.
        _ = window.NeedsRecentre(window.Min, 64f);
        var issues = new List<TileWorldIssue>();
        HashSet<TileCoord> indoor = IndoorTiles();
        ExcludeUnsafeWalls(indoor, window, issues);
        List<PlacedRoof> roofs = Roofs(window, issues);
        var volumes = new List<InteriorVolume>();
        float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
        foreach (IndoorRect rect in Rectangles(indoor))
        {
            float floor = MinimumCorner(rect.Tiles, rect.Planes.Lowest);
            int roofPlane = rect.Planes.Top + 1;
            float ceiling = roofPlane < _document.PlaneCount
                ? MinimumCorner(rect.Tiles, roofPlane)
                : MinimumCorner(rect.Tiles, rect.Planes.Top) + _document.PlaneHeight;
            var assigned = new List<InteriorRoof>();
            float top = ceiling;
            foreach (PlacedRoof roof in roofs)
                if (rect.Planes.RoofEligible && roof.Plane == roofPlane && roof.Tiles.Intersects(rect.Tiles))
                {
                    assigned.Add(roof.Roof);
                    top = Math.Max(top, roof.HighestY);
                }
            lowest = Math.Min(lowest, floor);
            highest = Math.Max(highest, top);
            if (Touches(rect.Tiles, window))
                volumes.Add(new InteriorVolume
                {
                    Outline = Outline(rect.Tiles),
                    FloorY = floor,
                    CeilingY = ceiling,
                    Roofs = assigned.ToArray(),
                });
        }

        if (float.IsPositiveInfinity(lowest)) lowest = highest = 0f;
        long digest = 0;
        foreach (RegionCoord region in _document.Regions.Keys.OrderBy(r => r.Rz).ThenBy(r => r.Rx))
            for (int plane = 0; plane < _document.PlaneCount; plane++)
                digest = unchecked(digest * 31 + TileInteriorInputs.Digest(_document, _catalogs, region, plane));
        // File refresh can replace roof geometry without an authored document edit.
        if (_encodingDigest != digest || _encodingBounds != (lowest, highest))
        {
            _encoding = InteriorEncoding.For(lowest, highest);
            _encodingDigest = digest;
            _encodingBounds = (lowest, highest);
        }
        if (_encoding.StepY > 0.01f)
            issues.Add(new TileWorldIssue("interior.step", string.Create(CultureInfo.InvariantCulture,
                $"source height range {highest - lowest:0.###} m needs step {_encoding.StepY:0.######} m"), null, null));
        LastIssues = issues.ToArray();
        return new InteriorSourceResult(volumes.ToArray(), _encoding,
            issues.Select(i => $"{i.Code}: {i.Message}").ToArray());
    }

    HashSet<TileCoord> IndoorTiles()
    {
        var indoor = new HashSet<TileCoord>();
        foreach (TileRegion region in _document.Regions.Values)
            for (int p = 0; p < _document.PlaneCount; p++)
            {
                if (region.Plane(p).Settings is not { } settings) continue;
                for (int i = 0; i < settings.Length; i++)
                    if ((settings[i] & (byte)TileSettings.Indoors) != 0)
                        indoor.Add(new TileCoord(region.Coord.OriginX + i % TileRegion.Size,
                            region.Coord.OriginZ + i / TileRegion.Size, p));
            }
        return indoor;
    }

    void ExcludeUnsafeWalls(HashSet<TileCoord> indoor, InteriorWindow window, List<TileWorldIssue> issues)
    {
        var walls = new List<(TileObject Object, int Edges, bool Diagonal)>();
        var owned = new HashSet<(TileCoord Tile, TileDirection Edge)>();
        foreach (TileObject o in _document.AllObjects().OrderBy(o => o.Id))
        {
            TileCollisionKind? kind = _catalogs.Archetype(o.ArchetypeId)?.CollisionKind;
            if (kind is not (TileCollisionKind.Wall or TileCollisionKind.WallCorner or TileCollisionKind.Diagonal)) continue;
            bool diagonal = kind == TileCollisionKind.Diagonal;
            int edges = diagonal ? 0 : 1 << (int)TileCollisionBaker.WallFacing(o.Rotation);
            if (kind == TileCollisionKind.WallCorner) edges |= 1 << (int)TileCollisionBaker.WallFacing(o.Rotation + 1);
            walls.Add((o, edges, diagonal));
            for (int e = 0; e < 4; e++)
            {
                if ((edges & (1 << e)) == 0) continue;
                var edge = (TileDirection)e;
                (int dx, int dz) = TileDirections.Delta(edge);
                owned.Add((o.Coord, edge));
                owned.Add((o.Coord.Offset(dx, dz), Opposite(edge)));
            }
        }
        var excluded = new HashSet<TileCoord>();
        foreach ((TileObject o, int edges, bool diagonal) in walls)
        {
            if (!indoor.Contains(o.Coord)) continue;
            bool unsafeEdge = false;
            for (int e = 0; e < 4; e++)
            {
                var edge = (TileDirection)e;
                if (!diagonal && (edges & (1 << e)) == 0) continue;
                TileDirection opposite = diagonal ? edge : Opposite(edge);
                (int dx, int dz) = TileDirections.Delta(opposite);
                if (!indoor.Contains(o.Coord.Offset(dx, dz)) && (diagonal || !owned.Contains((o.Coord, opposite))))
                    unsafeEdge = true;
            }
            if (!unsafeEdge) continue;
            excluded.Add(o.Coord);
            if (Touches(new TileRect(o.X, o.Z, 1, 1), window))
                issues.Add(ObjectIssue(diagonal ? "interior.diagonal-wall" : "interior.wall-inner-edge", o,
                    diagonal ? "diagonal boundary tile is excluded from the interior" : "wall is on an inner boundary edge, tile is excluded from the interior"));
        }
        indoor.ExceptWith(excluded);
    }

    List<PlacedRoof> Roofs(InteriorWindow window, List<TileWorldIssue> issues)
    {
        var roofs = new List<PlacedRoof>();
        var loaded = new Dictionary<string, (ClearanceSamples? Samples, string? Error)>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (TileObject o in _document.AllObjects().OrderBy(o => o.Id))
        {
            if (_catalogs.Archetype(o.ArchetypeId) is not { IsRoof: true } a) continue;
            TileRect footprint = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
            string meshRef = a.MeshRef ?? string.Empty;
            if (!loaded.TryGetValue(meshRef, out var read))
            {
                ClearanceSamples? samples = _samples.Get(_meshes, meshRef, window.CellSize, out string? error);
                loaded.Add(meshRef, read = (samples, error));
            }
            if (read.Samples is null)
            {
                if (Touches(footprint, window) && reported.Add(a.Id))
                    issues.Add(ObjectIssue("interior.roof-mesh", o, $"mesh '{a.MeshRef}' cannot be read: {read.Error}"));
                continue;
            }
            Matrix4x4 toWorld = TileObjectPlacement.LocalToWorld(_document, a, o);
            float highest = float.NegativeInfinity;
            foreach (Vector3 point in read.Samples.Points)
                highest = Math.Max(highest, Vector3.Transform(point, toWorld).Y);
            roofs.Add(new PlacedRoof(footprint, o.Plane, new InteriorRoof(read.Samples, toWorld), highest));
        }
        return roofs;
    }

    List<IndoorRect> Rectangles(HashSet<TileCoord> indoor)
    {
        var columns = new Dictionary<(int X, int Z), Planes>();
        foreach (TileCoord tile in indoor)
        {
            var key = (tile.X, tile.Z);
            if (!columns.TryGetValue(key, out Planes p) || tile.Plane < p.Lowest)
                columns[key] = new Planes(tile.Plane, tile.Plane, false);
        }
        var origins = columns.Keys.OrderBy(k => k.Z).ThenBy(k => k.X).ToArray();
        foreach (var origin in origins)
        {
            int lowest = columns[origin].Lowest, top = lowest;
            // A rejected or absent storey ends the span, even when indoor planes remain above it.
            while (top + 1 < _document.PlaneCount && indoor.Contains(new TileCoord(origin.X, origin.Z, top + 1))) top++;
            bool roofEligible = true;
            for (int plane = top + 1; plane < _document.PlaneCount; plane++)
                if ((_document.GetSettings(origin.X, origin.Z, plane) & TileSettings.Indoors) != 0)
                {
                    roofEligible = false;
                    break;
                }
            columns[origin] = new Planes(lowest, top, roofEligible);
        }
        var rectangles = new List<IndoorRect>();
        foreach (var origin in origins)
        {
            if (!columns.TryGetValue(origin, out Planes planes)) continue;
            int width = 1, height = 1;
            while (columns.TryGetValue((origin.X + width, origin.Z), out Planes next) && next == planes) width++;
            bool fullRow = true;
            while (fullRow)
            {
                for (int x = origin.X; x < origin.X + width; x++)
                    if (!columns.TryGetValue((x, origin.Z + height), out Planes next) || next != planes)
                    {
                        fullRow = false;
                        break;
                    }
                if (fullRow) height++;
            }
            var rect = new TileRect(origin.X, origin.Z, width, height);
            rectangles.Add(new IndoorRect(rect, planes));
            for (int z = rect.Z; z < rect.Z1; z++)
                for (int x = rect.X; x < rect.X1; x++) columns.Remove((x, z));
        }
        return rectangles;
    }

    float MinimumCorner(TileRect rect, int plane)
    {
        float minimum = float.PositiveInfinity;
        for (int z = rect.Z; z <= rect.Z1; z++)
            for (int x = rect.X; x <= rect.X1; x++) minimum = Math.Min(minimum, _document.CornerHeight(x, z, plane));
        return minimum;
    }

    Vector2[] Outline(TileRect rect) =>
    [
        new(TileWorldSpace.WorldX(rect.X, _document.TileSize), TileWorldSpace.WorldZ(rect.Z, _document.TileSize)),
        new(TileWorldSpace.WorldX(rect.X1, _document.TileSize), TileWorldSpace.WorldZ(rect.Z, _document.TileSize)),
        new(TileWorldSpace.WorldX(rect.X1, _document.TileSize), TileWorldSpace.WorldZ(rect.Z1, _document.TileSize)),
        new(TileWorldSpace.WorldX(rect.X, _document.TileSize), TileWorldSpace.WorldZ(rect.Z1, _document.TileSize)),
    ];

    bool Touches(TileRect rect, InteriorWindow window)
    {
        float extent = window.CellSize * window.Cells;
        return TileWorldSpace.WorldX(rect.X, _document.TileSize) <= window.Min.X + extent &&
            TileWorldSpace.WorldX(rect.X1, _document.TileSize) >= window.Min.X &&
            TileWorldSpace.WorldZ(rect.Z1, _document.TileSize) <= window.Min.Y + extent &&
            TileWorldSpace.WorldZ(rect.Z, _document.TileSize) >= window.Min.Y;
    }

    static TileWorldIssue ObjectIssue(string code, TileObject o, string reason) => new(code,
        $"{o.ArchetypeId} #{o.Id} at ({o.X}, {o.Z}) plane {o.Plane}: {reason}", o.Coord.Region, o.Coord);

    static TileDirection Opposite(TileDirection edge) => edge switch
    {
        TileDirection.W => TileDirection.E,
        TileDirection.E => TileDirection.W,
        TileDirection.N => TileDirection.S,
        _ => TileDirection.N,
    };

    readonly record struct Planes(int Lowest, int Top, bool RoofEligible);
    readonly record struct IndoorRect(TileRect Tiles, Planes Planes);
    readonly record struct PlacedRoof(TileRect Tiles, int Plane, InteriorRoof Roof, float HighestY);
}
