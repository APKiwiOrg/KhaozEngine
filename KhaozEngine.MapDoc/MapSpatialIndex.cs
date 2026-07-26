using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Buckets a loaded document's point content by document tile, once. O(n) to build, O(k) per query.
/// Works on a monolithic and a tiled document alike, so the editor, the MCP tool and a small game keep a
/// whole-document workflow and still get region queries. The document-layer analogue of the internal
/// <c>PlacementBuckets</c> the chunk sink already uses.</summary>
public sealed class MapSpatialIndex
{
    static readonly IReadOnlyList<MapPlacement> NoPlacements = Array.Empty<MapPlacement>();
    static readonly IReadOnlyList<MapSpawn> NoSpawns = Array.Empty<MapSpawn>();
    static readonly IReadOnlyList<MapPlayerSpawn> NoPlayerSpawns = Array.Empty<MapPlayerSpawn>();
    static readonly IReadOnlyList<MapSculptTile> NoSculpt = Array.Empty<MapSculptTile>();

    readonly Dictionary<MapTileCoord, List<MapPlacement>> _placements = new();
    readonly Dictionary<MapTileCoord, List<MapSpawn>> _spawns = new();
    readonly Dictionary<MapTileCoord, List<MapPlayerSpawn>> _playerSpawns = new();
    readonly Dictionary<MapTileCoord, List<MapSculptTile>> _sculpt = new();
    readonly MapTileCoord[] _occupied;

    MapSpatialIndex(MapDocument doc)
    {
        TileSize = doc.TileSize;
        SculptCellSize = doc.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;

        foreach (MapPlacement p in doc.Placements)
            Bucket(_placements, MapTileGrid.CoordOf(p.X, p.Z, TileSize)).Add(p);
        foreach (MapSpawn s in doc.Spawns)
            Bucket(_spawns, MapTileGrid.CoordOf(s.X, s.Z, TileSize)).Add(s);
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
            Bucket(_playerSpawns, MapTileGrid.CoordOf(s.X, s.Z, TileSize)).Add(s);
        if (doc.TerrainOverrides is { } overrides)
            foreach (MapSculptTile t in overrides.Tiles)
                Bucket(_sculpt, MapTileGrid.OwnerOfSculptTile(t.TileX, t.TileZ, SculptCellSize, TileSize)).Add(t);

        var occupied = new HashSet<MapTileCoord>();
        foreach (MapTileCoord c in _placements.Keys) occupied.Add(c);
        foreach (MapTileCoord c in _spawns.Keys) occupied.Add(c);
        foreach (MapTileCoord c in _playerSpawns.Keys) occupied.Add(c);
        foreach (MapTileCoord c in _sculpt.Keys) occupied.Add(c);
        _occupied = new MapTileCoord[occupied.Count];
        occupied.CopyTo(_occupied);
        Array.Sort(_occupied, static (a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
    }

    /// <summary>Buckets the document's point content by document tile.</summary>
    public static MapSpatialIndex Build(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return new MapSpatialIndex(doc);
    }

    /// <summary>The document tile edge, in world meters, the buckets were built on.</summary>
    public float TileSize { get; }

    /// <summary>The sculpt cell size the sculpt-tile ownership rule was resolved against, taken from the
    /// document's override block (or <see cref="MapTerrainOverrides.DefaultCellSize"/> when it has none).</summary>
    public float SculptCellSize { get; }

    /// <summary>The occupied tiles, ascending (Z, then X). ORDERED, and a list rather than a collection,
    /// because the monolithic world-hash path composes straight off this: an insertion-ordered
    /// <c>Dictionary.Keys</c> would make the same world built in two authoring orders hash differently.</summary>
    public IReadOnlyList<MapTileCoord> OccupiedTiles => _occupied;

    public IReadOnlyList<MapPlacement> PlacementsIn(MapTileCoord tile) =>
        _placements.TryGetValue(tile, out List<MapPlacement>? l) ? l : NoPlacements;

    public IReadOnlyList<MapSpawn> SpawnsIn(MapTileCoord tile) =>
        _spawns.TryGetValue(tile, out List<MapSpawn>? l) ? l : NoSpawns;

    public IReadOnlyList<MapPlayerSpawn> PlayerSpawnsIn(MapTileCoord tile) =>
        _playerSpawns.TryGetValue(tile, out List<MapPlayerSpawn>? l) ? l : NoPlayerSpawns;

    public IReadOnlyList<MapSculptTile> SculptTilesIn(MapTileCoord tile) =>
        _sculpt.TryGetValue(tile, out List<MapSculptTile>? l) ? l : NoSculpt;

    /// <summary>Appends every placement whose (X, Z) falls in the HALF-OPEN rect into a caller-owned list, so
    /// a per-frame query allocates nothing. Ascending (Z, then X) by tile, document order within a tile.</summary>
    public void PlacementsIn(RectArea area, List<MapPlacement> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        ForEachTile(area, tile =>
        {
            foreach (MapPlacement p in PlacementsIn(tile))
                if (InArea(p.X, p.Z, area)) into.Add(p);
        });
    }

    /// <summary>Appends every spawn whose (X, Z) falls in the HALF-OPEN rect into a caller-owned list.</summary>
    public void SpawnsIn(RectArea area, List<MapSpawn> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        ForEachTile(area, tile =>
        {
            foreach (MapSpawn s in SpawnsIn(tile))
                if (InArea(s.X, s.Z, area)) into.Add(s);
        });
    }

    /// <summary>True when the point falls in the half-open [Min, Max) rect on both axes.</summary>
    internal static bool InArea(float x, float z, RectArea area) =>
        x >= area.MinX && x < area.MaxX && z >= area.MinZ && z < area.MaxZ;

    void ForEachTile(RectArea area, Action<MapTileCoord> body)
    {
        MapTileRect range = MapTileGrid.RectOf(area, TileSize);
        for (int z = range.Min.Z; z <= range.Max.Z; z++)
            for (int x = range.Min.X; x <= range.Max.X; x++)
                body(new MapTileCoord(x, z));
    }

    static List<T> Bucket<T>(Dictionary<MapTileCoord, List<T>> map, MapTileCoord coord)
    {
        if (!map.TryGetValue(coord, out List<T>? list)) map[coord] = list = new List<T>();
        return list;
    }
}
