using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Immutable clearance grid over a rectangular slice of the XZ world plane. Row-major storage: cell
/// (cx, cz) lives at index cz * Width + cx and covers the grid-local rectangle
/// [c * CellSize, (c + 1) * CellSize) on each axis. Local XZ rotates by YawRadians before
/// translation to (OriginX, OriginZ). Each cell stores a clearance byte, the approximate distance from
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
    readonly float[]? _heights;
    readonly float _cosYaw;
    readonly float _sinYaw;

    /// <summary>World size of one cell, on both axes (world units).</summary>
    public float CellSize { get; }

    /// <summary>World X of the grid-local (0, 0) corner and rotation pivot.</summary>
    public float OriginX { get; }

    /// <summary>World Z of the grid-local (0, 0) corner and rotation pivot.</summary>
    public float OriginZ { get; }

    /// <summary>Finite rotation in radians. Positive yaw turns local +X toward world +Z.</summary>
    public float YawRadians { get; }

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

    NavGrid(byte[] clearance, int width, int height, float cellSize, float originX, float originZ, float yMin, float yMax, float[]? heights = null, float yawRadians = 0)
    {
        _clearance = clearance;
        Width = width;
        Height = height;
        CellSize = cellSize;
        OriginX = originX;
        OriginZ = originZ;
        YMin = yMin;
        YMax = yMax;
        _heights = heights;
        YawRadians = yawRadians;
        (_sinYaw, _cosYaw) = MathF.SinCos(yawRadians);
    }

    /// <summary>
    /// Rasterizes <paramref name="walkable"/> over a <paramref name="width"/> by <paramref name="height"/> grid
    /// of <paramref name="cellSize"/> world units anchored at (<paramref name="originX"/>, <paramref name="originZ"/>),
    /// then bakes the clearance transform once. <paramref name="walkable"/> is called once per cell with its
    /// (cx, cz) grid coordinates and returns false for a blocked cell. <paramref name="yMin"/> and
    /// <paramref name="yMax"/> record the vertical band this grid represents, checked later via
    /// <see cref="ContainsY"/>. <paramref name="yawRadians"/> rotates grid-local XZ before translation.
    /// </summary>
    public static NavGrid FromWalkable(
        int width, int height, float cellSize, float originX, float originZ,
        Func<int, int, bool> walkable,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity, float yawRadians = 0f)
    {
        if (walkable is null) throw new ArgumentNullException(nameof(walkable));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");

        if (!float.IsFinite(yawRadians)) throw new ArgumentOutOfRangeException(nameof(yawRadians));

        var blocked = new bool[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
                blocked[z * width + x] = !walkable(x, z);

        byte[] clearance = ClearanceTransform.Compute(blocked, width, height);
        return new NavGrid(clearance, width, height, cellSize, originX, originZ, yMin, yMax, yawRadians: yawRadians);
    }

    /// <summary>
    /// Rasterizes <paramref name="sample"/> over a <paramref name="width"/> by <paramref name="height"/>
    /// grid of <paramref name="cellSize"/> world units anchored at (<paramref name="originX"/>,
    /// <paramref name="originZ"/>), then applies the step-reachability and headroom rules
    /// (<paramref name="stepHeight"/> / <paramref name="agentHeight"/>, see <see cref="StepMask"/>) and
    /// bakes the clearance transform once. <paramref name="sample"/> is called once per cell with its
    /// (cx, cz) grid coordinates. Unlike <see cref="FromWalkable"/>, the resulting grid records the
    /// per-cell surface heights (see <see cref="SurfaceHeightAt"/> / <see cref="HasSurfaceHeights"/>).
    /// A cell is blocked when its sample is not standable, its headroom is below
    /// <paramref name="agentHeight"/>, or its surface drops to a standable neighbor by more than
    /// <paramref name="stepHeight"/>. The one exception is a lone standable top, every standable neighbor
    /// more than a step below it and every neighbor blocked, which stays passable so a
    /// <see cref="NavLinkKind.Hop"/> link can still reach it (see <see cref="StepMask"/>).
    /// <paramref name="yMin"/> and <paramref name="yMax"/> record the
    /// vertical band, checked later via <see cref="ContainsY"/>. <paramref name="yawRadians"/> rotates
    /// grid-local XZ before translation. It does not change sampled heights or cell topology.
    /// </summary>
    public static NavGrid FromSurfaces(
        int width, int height, float cellSize, float originX, float originZ,
        Func<int, int, NavSurfaceSample> sample,
        float stepHeight, float agentHeight,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity, float yawRadians = 0f)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");

        if (!float.IsFinite(yawRadians)) throw new ArgumentOutOfRangeException(nameof(yawRadians));

        var standable = new bool[width * height];
        var heights = new float[width * height];
        var headroom = new float[width * height];
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                NavSurfaceSample s = sample(x, z);
                standable[i] = s.Standable;
                heights[i] = s.Standable ? s.Height : 0f;
                headroom[i] = s.Headroom;
            }
        }

        bool[] blocked = StepMask.Compute(standable, heights, headroom, width, height, stepHeight, agentHeight);
        byte[] clearance = ClearanceTransform.Compute(blocked, width, height);
        return new NavGrid(clearance, width, height, cellSize, originX, originZ, yMin, yMax, heights, yawRadians);
    }

    /// <summary>True when (<paramref name="cx"/>, <paramref name="cz"/>) is within the grid.</summary>
    public bool InBounds(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Width && cz < Height;

    /// <summary>Clearance at (<paramref name="cx"/>, <paramref name="cz"/>) in half-cell units. 0 when the
    /// cell is blocked or out of bounds (space outside the grid counts as blocked).</summary>
    public byte ClearanceAt(int cx, int cz) => InBounds(cx, cz) ? _clearance[cz * Width + cx] : (byte)0;

    /// <summary>True when this grid was baked with a per-cell surface height field
    /// (via <see cref="FromSurfaces"/>). False for grids from <see cref="FromWalkable"/>.</summary>
    public bool HasSurfaceHeights => _heights is not null;

    /// <summary>
    /// The baked surface height at (<paramref name="cx"/>, <paramref name="cz"/>) in world Y, or null when
    /// this grid has no height field (<see cref="HasSurfaceHeights"/> is false), the cell is out of bounds,
    /// or the cell is blocked (clearance 0). A non-null result is the surface an agent stands on there.
    /// </summary>
    public float? SurfaceHeightAt(int cx, int cz)
    {
        if (_heights is null) return null;
        if (!InBounds(cx, cz)) return null;
        if (ClearanceAt(cx, cz) == 0) return null;
        return _heights[cz * Width + cx];
    }

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
    {
        Vector2 local = WorldToLocal(new Vector2(x, z));
        return ((int)MathF.Floor(local.X / CellSize), (int)MathF.Floor(local.Y / CellSize));
    }

    internal Vector2 WorldToLocal(Vector2 world)
    {
        float x = world.X - OriginX;
        float z = world.Y - OriginZ;
        return new Vector2(x * _cosYaw + z * _sinYaw, -x * _sinYaw + z * _cosYaw);
    }

    /// <summary>World-space center of cell (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
    public Vector2 CellCenter(int cx, int cz)
    {
        float x = (cx + 0.5f) * CellSize;
        float z = (cz + 0.5f) * CellSize;
        return new Vector2(OriginX + x * _cosYaw - z * _sinYaw, OriginZ + x * _sinYaw + z * _cosYaw);
    }

    /// <summary>True when <paramref name="y"/> falls within [<see cref="YMin"/>, <see cref="YMax"/>].</summary>
    public bool ContainsY(float y) => y >= YMin && y <= YMax;
}
