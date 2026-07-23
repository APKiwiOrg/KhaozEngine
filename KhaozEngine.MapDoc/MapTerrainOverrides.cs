using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The document's terrain sculpt/delta layer (map format v2): a sparse map of
/// <see cref="TerrainSculpt.TileSize"/>-cell tiles of authored height deltas at a chosen cell size, folded
/// into the analytic terrain by <see cref="MapRuntime.BuildField"/>. Only touched tiles are stored, so an
/// absent block or an empty map leaves terrain byte-identical to the analytic field. Deltas are meters,
/// authored per cell through the set/add API and bilinearly sampled between cell centers at runtime
/// (<see cref="TerrainSculpt"/>). A global cell (cellX, cellZ) has its center at world
/// (cellX * <see cref="CellSize"/>, cellZ * CellSize). This is the authoring surface; the editor and MCP
/// verbs edit it, and it serializes as { cellSize, tiles[] }.</summary>
[JsonConverter(typeof(MapTerrainOverridesConverter))]
public sealed class MapTerrainOverrides
{
    /// <summary>The default sculpt cell size (world meters) when a document does not state one.</summary>
    public const float DefaultCellSize = 0.5f;

    readonly Dictionary<long, MapSculptTile> _tiles = new();

    /// <summary>Creates an empty override layer at the given cell size (default <see cref="DefaultCellSize"/>),
    /// which becomes the block header.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is not positive and finite.</exception>
    public MapTerrainOverrides(float cellSize = DefaultCellSize)
    {
        if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "sculpt cell size must be positive and finite.");
        CellSize = cellSize;
    }

    /// <summary>World size of one sculpt cell, in meters. Fixed at construction (the block header): a zone
    /// picks it once so a coarser or finer authoring grid can be chosen per document.</summary>
    public float CellSize { get; }

    /// <summary>Number of stored delta tiles.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>True when no tile has been authored, so the layer adds nothing to the analytic terrain.</summary>
    public bool IsEmpty => _tiles.Count == 0;

    /// <summary>The stored tiles in ascending (tileZ, then tileX) order: deterministic for save output and
    /// for rebuilding the runtime sculpt. A fresh snapshot each call.</summary>
    public IReadOnlyList<MapSculptTile> Tiles
    {
        get
        {
            var list = new List<MapSculptTile>(_tiles.Values);
            list.Sort(static (a, b) => a.TileZ != b.TileZ ? a.TileZ.CompareTo(b.TileZ) : a.TileX.CompareTo(b.TileX));
            return list;
        }
    }

    /// <summary>Sets the absolute height delta (meters) at a global cell, creating the covering tile if
    /// needed.</summary>
    public void SetDelta(int cellX, int cellZ, float delta)
    {
        MapSculptTile tile = TileFor(cellX, cellZ, create: true)!;
        tile[LocalX(cellX), LocalZ(cellZ)] = delta;
    }

    /// <summary>Adds to the height delta (meters) at a global cell, creating the covering tile if needed.</summary>
    public void AddDelta(int cellX, int cellZ, float delta)
    {
        MapSculptTile tile = TileFor(cellX, cellZ, create: true)!;
        int lx = LocalX(cellX), lz = LocalZ(cellZ);
        tile[lx, lz] += delta;
    }

    /// <summary>The height delta (meters) at a global cell, 0 where no tile covers it.</summary>
    public float GetDelta(int cellX, int cellZ)
    {
        MapSculptTile? tile = TileFor(cellX, cellZ, create: false);
        return tile is null ? 0f : tile[LocalX(cellX), LocalZ(cellZ)];
    }

    /// <summary>Looks up a stored tile by tile coordinate.</summary>
    public bool TryGetTile(int tileX, int tileZ, out MapSculptTile tile)
    {
        bool found = _tiles.TryGetValue(Key(tileX, tileZ), out MapSculptTile? t);
        tile = t!;
        return found;
    }

    /// <summary>Adds or replaces a whole tile (used by the loader). The tile is stored as-is, keyed by its
    /// own coordinate.</summary>
    internal void PutTile(MapSculptTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        _tiles[Key(tile.TileX, tile.TileZ)] = tile;
    }

    MapSculptTile? TileFor(int cellX, int cellZ, bool create)
    {
        int tx = FloorDivTile(cellX), tz = FloorDivTile(cellZ);
        long key = Key(tx, tz);
        if (_tiles.TryGetValue(key, out MapSculptTile? tile)) return tile;
        if (!create) return null;
        tile = new MapSculptTile(tx, tz);
        _tiles[key] = tile;
        return tile;
    }

    static int LocalX(int cellX) => cellX - FloorDivTile(cellX) * TerrainSculpt.TileSize;
    static int LocalZ(int cellZ) => cellZ - FloorDivTile(cellZ) * TerrainSculpt.TileSize;

    /// <summary>Floor-divide a global cell index by the tile size, correct for negative cells.</summary>
    static int FloorDivTile(int cell) => cell >= 0 ? cell / TerrainSculpt.TileSize : (cell - (TerrainSculpt.TileSize - 1)) / TerrainSculpt.TileSize;

    static long Key(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;
}
