using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Immutable clearance grid over a rectangular slice of the XZ world plane. Row-major storage: cell
/// (cx, cz) lives at index cz * Width + cx and covers the world-space rectangle
/// [Origin + c * CellSize, Origin + (c + 1) * CellSize) on each axis, with its center at
/// Origin + (c + 0.5) * CellSize. Each cell stores a clearance byte, the approximate distance from
/// the cell center to the nearest blocked cell center in half-cell units (2-3 chamfer, see
/// <see cref="ClearanceTransform"/>), saturated at 255. A blocked cell stores 0, and space outside
/// the grid counts as blocked, so clearance falls off toward the borders. In world meters,
/// clearanceMeters = stored * CellSize * 0.5f, and a cell is passable for an agent of a given radius
/// when its clearance is nonzero and clearanceMeters is at least that radius. Baked once via
/// <see cref="FromWalkable"/> and read many times, so pathfinding never re-touches the walkable
/// predicate. Render-free, deterministic.
/// </summary>
public sealed class NavGrid
{
    readonly byte[] _clearance;

    /// <summary>World size of one cell, on both axes (world units).</summary>
    public float CellSize { get; }

    /// <summary>World X of the grid origin, cell (0, 0)'s minimum X corner.</summary>
    public float OriginX { get; }

    /// <summary>World Z of the grid origin, cell (0, 0)'s minimum Z corner.</summary>
    public float OriginZ { get; }

    /// <summary>Grid width in cells (X axis).</summary>
    public int Width { get; }

    /// <summary>Grid height in cells (Z axis).</summary>
    public int Height { get; }

    /// <summary>Lower world Y bound this grid represents. Default <see cref="float.NegativeInfinity"/>
    /// (no lower bound). See <see cref="ContainsY"/>.</summary>
    public float YMin { get; }

    /// <summary>Upper world Y bound this grid represents. Default <see cref="float.PositiveInfinity"/>
    /// (no upper bound). See <see cref="ContainsY"/>.</summary>
    public float YMax { get; }

    NavGrid(byte[] clearance, int width, int height, float cellSize, float originX, float originZ, float yMin, float yMax)
    {
        _clearance = clearance;
        Width = width;
        Height = height;
        CellSize = cellSize;
        OriginX = originX;
        OriginZ = originZ;
        YMin = yMin;
        YMax = yMax;
    }

    /// <summary>
    /// Rasterizes <paramref name="walkable"/> over a <paramref name="width"/> by <paramref name="height"/> grid
    /// of <paramref name="cellSize"/> world units anchored at (<paramref name="originX"/>, <paramref name="originZ"/>),
    /// then bakes the clearance transform once. <paramref name="walkable"/> is called once per cell with its
    /// (cx, cz) grid coordinates and returns false for a blocked cell. <paramref name="yMin"/> and
    /// <paramref name="yMax"/> record the vertical band this grid represents, checked later via
    /// <see cref="ContainsY"/>.
    /// </summary>
    public static NavGrid FromWalkable(
        int width, int height, float cellSize, float originX, float originZ,
        Func<int, int, bool> walkable,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
    {
        if (walkable is null) throw new ArgumentNullException(nameof(walkable));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");

        var blocked = new bool[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
                blocked[z * width + x] = !walkable(x, z);

        byte[] clearance = ClearanceTransform.Compute(blocked, width, height);
        return new NavGrid(clearance, width, height, cellSize, originX, originZ, yMin, yMax);
    }

    /// <summary>True when (<paramref name="cx"/>, <paramref name="cz"/>) is within the grid.</summary>
    public bool InBounds(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Width && cz < Height;

    /// <summary>Clearance at (<paramref name="cx"/>, <paramref name="cz"/>) in half-cell units. 0 when the
    /// cell is blocked or out of bounds (space outside the grid counts as blocked).</summary>
    public byte ClearanceAt(int cx, int cz) => InBounds(cx, cz) ? _clearance[cz * Width + cx] : (byte)0;

    /// <summary>Clearance at (<paramref name="cx"/>, <paramref name="cz"/>) converted to world meters:
    /// <see cref="ClearanceAt"/> * <see cref="CellSize"/> * 0.5f.</summary>
    public float ClearanceMetersAt(int cx, int cz) => ClearanceAt(cx, cz) * CellSize * 0.5f;

    /// <summary>True when an agent of <paramref name="agentRadius"/> fits at
    /// (<paramref name="cx"/>, <paramref name="cz"/>): the cell is not blocked and its clearance in meters
    /// is at least the radius.</summary>
    public bool IsPassable(int cx, int cz, float agentRadius)
        => ClearanceAt(cx, cz) > 0 && ClearanceMetersAt(cx, cz) >= agentRadius;

    /// <summary>The grid cell containing world position (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public (int X, int Z) CellOf(float x, float z)
        => ((int)MathF.Floor((x - OriginX) / CellSize), (int)MathF.Floor((z - OriginZ) / CellSize));

    /// <summary>World-space center of cell (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
    public Vector2 CellCenter(int cx, int cz)
        => new(OriginX + (cx + 0.5f) * CellSize, OriginZ + (cz + 0.5f) * CellSize);

    /// <summary>True when <paramref name="y"/> falls within [<see cref="YMin"/>, <see cref="YMax"/>].</summary>
    public bool ContainsY(float y) => y >= YMin && y <= YMax;
}
