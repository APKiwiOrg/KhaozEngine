using System;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Integer index of a square document tile. (X, Z) maps to the world region whose -X/-Z corner is
/// (X * tileSize, Z * tileSize). Same floor(world / size) convention as <see cref="ChunkCoord"/> and
/// Sharding's CellCoord, deliberately a DISTINCT type so a chunk coord cannot be passed where a tile coord
/// is meant: a 60 m chunk grid and a 512 m document grid would otherwise share a type in which
/// <c>(3, 4)</c> means two different rects with no compile-time distinction.</summary>
public readonly record struct MapTileCoord(int X, int Z);

/// <summary>Inclusive rectangular range of document tiles.</summary>
public readonly record struct MapTileRect(MapTileCoord Min, MapTileCoord Max)
{
    /// <summary>True when the tile falls inside the inclusive range on both axes.</summary>
    public bool Contains(MapTileCoord c) =>
        c.X >= Min.X && c.X <= Max.X && c.Z >= Min.Z && c.Z <= Max.Z;

    /// <summary>Number of tile coordinates in the range, 0 when the range is inverted on either axis.</summary>
    public int Count
    {
        get
        {
            long w = (long)Max.X - Min.X + 1;
            long h = (long)Max.Z - Min.Z + 1;
            return w <= 0 || h <= 0 ? 0 : (int)Math.Min(w * h, int.MaxValue);
        }
    }
}

/// <summary>Document-tile grid math. Delegates to <see cref="ChunkGrid"/> so the floor rule has exactly one
/// implementation and the two grids can never drift apart.</summary>
public static class MapTileGrid
{
    /// <summary>The document tile containing the world point. Floors toward negative infinity, so a point on a
    /// tile's lower edge belongs to that tile and negatives floor downward, not toward zero.</summary>
    public static MapTileCoord CoordOf(float worldX, float worldZ, float tileSize)
    {
        ChunkCoord c = ChunkGrid.CoordOf(worldX, worldZ, tileSize);
        return new MapTileCoord(c.X, c.Z);
    }

    /// <summary>The half-open [origin, origin + size) world rect of the tile, matching
    /// <see cref="ChunkGrid.AreaOf"/>'s streaming invariant: a point exactly on the max edge belongs to the
    /// next tile, which is what makes a partition of rects reproduce the whole document exactly.</summary>
    public static RectArea AreaOf(MapTileCoord c, float tileSize) =>
        ChunkGrid.AreaOf(new ChunkCoord(c.X, c.Z), tileSize);

    /// <summary>World XZ midpoint of the tile.</summary>
    public static Vector2 CenterOf(MapTileCoord c, float tileSize) =>
        ChunkGrid.CenterOf(new ChunkCoord(c.X, c.Z), tileSize);

    /// <summary>The INCLUSIVE tile range covering a world rect. Both corners floor, so a rect whose max edge
    /// sits exactly on a tile boundary yields one extra row or column: the range is a superset, never a
    /// subset, and callers filter by the half-open rect itself.</summary>
    public static MapTileRect RectOf(RectArea area, float tileSize) =>
        new(CoordOf(area.MinX, area.MinZ, tileSize), CoordOf(area.MaxX, area.MaxZ, tileSize));

    /// <summary>The document tile that OWNS a sculpt tile: the one containing the sculpt tile's origin corner.
    /// Single owner, no splitting of a delta array, deterministic for every sculpt cell size, including the
    /// ones where <paramref name="tileSize"/> is not an integer multiple of the sculpt span. A straddling
    /// sculpt tile therefore contributes deltas slightly outside its owning document tile, which is harmless
    /// because <see cref="TerrainSculpt"/> composites by world position and does not care which file a tile
    /// arrived in.</summary>
    public static MapTileCoord OwnerOfSculptTile(int sculptTileX, int sculptTileZ, float sculptCellSize, float tileSize)
    {
        float span = TerrainSculpt.TileSize * sculptCellSize;
        return CoordOf(sculptTileX * span, sculptTileZ * span, tileSize);
    }
}
