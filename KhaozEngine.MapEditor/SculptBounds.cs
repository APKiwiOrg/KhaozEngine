using System;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The inclusive range of global sculpt cells a brush may touch inside a document's bounds. The sculpt
/// layer stores 32-cell tiles, and <c>MapDocumentValidator</c> refuses a stored tile whose whole cell-centre
/// extent leaves the document bounds, so the brush must not touch any cell in a boundary-straddling tile. This
/// clamps the paintable region to the cells of the tiles that lie wholly within bounds, which keeps every tile the
/// brush creates saveable. A consequence of the tile-granular validator is a dead strip up to one tile
/// (<see cref="TerrainSculpt.TileSize"/> cells) wide along each edge whose bounds are not tile-aligned.</summary>
public readonly struct SculptBounds
{
    /// <summary>Smallest paintable global cell X (inclusive).</summary>
    public int MinCellX { get; }
    /// <summary>Smallest paintable global cell Z (inclusive).</summary>
    public int MinCellZ { get; }
    /// <summary>Largest paintable global cell X (inclusive).</summary>
    public int MaxCellX { get; }
    /// <summary>Largest paintable global cell Z (inclusive).</summary>
    public int MaxCellZ { get; }

    /// <summary>Builds the range directly from inclusive cell bounds.</summary>
    public SculptBounds(int minCellX, int minCellZ, int maxCellX, int maxCellZ)
    {
        MinCellX = minCellX;
        MinCellZ = minCellZ;
        MaxCellX = maxCellX;
        MaxCellZ = maxCellZ;
    }

    /// <summary>True when at least one whole tile fits on each axis, so the brush has cells to paint. False when
    /// the document is smaller than one tile on an axis (no paintable region there).</summary>
    public bool HasArea => MinCellX <= MaxCellX && MinCellZ <= MaxCellZ;

    /// <summary>The paintable cell range for a document extent [<paramref name="minX"/>..<paramref name="maxX"/>] x
    /// [<paramref name="minZ"/>..<paramref name="maxZ"/>] at the sculpt <paramref name="cellSize"/>: exactly the
    /// cells of the tiles that lie wholly within those bounds. A tiny epsilon absorbs float noise so a
    /// tile-aligned edge is not clipped by one tile.</summary>
    public static SculptBounds FromBounds(float minX, float minZ, float maxX, float maxZ, float cellSize)
    {
        if (!(cellSize > 0f)) return new SculptBounds(0, 0, -1, -1);
        const int span = TerrainSculpt.TileSize;
        float tileWorld = span * cellSize;
        const float eps = 1e-3f;
        int minTileX = (int)MathF.Ceiling(minX / tileWorld - eps);
        int maxTileX = (int)MathF.Floor((maxX / cellSize - (span - 1)) / span + eps);
        int minTileZ = (int)MathF.Ceiling(minZ / tileWorld - eps);
        int maxTileZ = (int)MathF.Floor((maxZ / cellSize - (span - 1)) / span + eps);
        return new SculptBounds(
            minTileX * span, minTileZ * span,
            maxTileX * span + (span - 1), maxTileZ * span + (span - 1));
    }
}
