using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>A conservative filled volume built from a <see cref="ClearanceMesh"/>, answering how deep a local
/// point sits inside it. The mesh's planar footprint is rasterized into square cells over
/// <c>[Min.x, Max.x] x [Min.z, Max.z]</c>. Every cell a surface sample lands in is filled, and its top is the
/// highest sample in it. The base of every cell is <see cref="Min"/>.y.
/// <para>
/// Filled-solid limit: a piece is solid everywhere below its top surface, down to the base. An overhang, an
/// open doorway, an arch or the space under eaves therefore counts as solid. That is the intended bias for an
/// authoring check that keeps one prop out of another, because it can only report a penetration that a precise
/// test would miss, never hide one.
/// </para>
/// <para>
/// Depth is the shortest way out: the distance down to the base, up to the cell top, or sideways to the
/// nearest empty cell, the last from a two-pass chamfer distance transform with weights 1 and sqrt 2.
/// Planar depth is quantized to the cell size, and a thin piece caps depth near half its thickness.
/// </para>
/// </summary>
public sealed class ClearanceSolid
{
    /// <summary>The largest planar grid <see cref="Build"/> accepts, counted as cells along x times cells along z.</summary>
    public const int MaxCells = 4_194_304;

    /// <summary>The smallest accepted <see cref="CellSize"/>, in local units.</summary>
    const float MinCellSize = 0.02f;

    /// <summary>The largest accepted <see cref="CellSize"/>, in local units.</summary>
    const float MaxCellSize = 1f;

    /// <summary>How far, in cells, a coordinate may stray past a cell edge and still count as on it. Absorbs the
    /// rounding a round trip through world space adds, so a point on a boundary keeps landing in the same cell.</summary>
    const double EdgeSlackCells = 1e-4;

    readonly int _cellsX;
    readonly int _cellsZ;
    readonly float[] _top;
    readonly float[] _planarDepth;

    ClearanceSolid(ClearanceMesh mesh, float cellSize, int cellsX, int cellsZ)
    {
        CellSize = cellSize;
        Min = mesh.Min;
        Max = mesh.Max;
        _cellsX = cellsX;
        _cellsZ = cellsZ;
        _top = new float[cellsX * cellsZ];
        _planarDepth = new float[cellsX * cellsZ];
        Array.Fill(_top, float.NegativeInfinity);
    }

    /// <summary>Side of one square planar cell, in the mesh's local units.</summary>
    public float CellSize { get; }

    /// <summary>The mesh's smallest corner. Its y is the base of every cell.</summary>
    public Vector3 Min { get; }

    /// <summary>The mesh's largest corner.</summary>
    public Vector3 Max { get; }

    /// <summary>Rasterize <paramref name="mesh"/> at <paramref name="cellSize"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> when <paramref name="cellSize"/> is outside 0.02..1 and
    /// <see cref="ClearanceTooLargeException"/> when the planar grid would exceed <see cref="MaxCells"/>.</summary>
    public static ClearanceSolid Build(ClearanceMesh mesh, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!(cellSize >= MinCellSize && cellSize <= MaxCellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must lie in 0.02..1.");

        double countX = AxisCells(mesh.Max.X - mesh.Min.X, cellSize);
        double countZ = AxisCells(mesh.Max.Z - mesh.Min.Z, cellSize);
        double total = countX * countZ;
        if (total > MaxCells)
            throw new ClearanceTooLargeException(total >= long.MaxValue ? long.MaxValue : (long)total);

        var solid = new ClearanceSolid(mesh, cellSize, (int)countX, (int)countZ);
        mesh.ForEachSample(cellSize / 2f, solid.Mark);
        solid.ComputePlanarDepth();
        return solid;
    }

    /// <summary>How deep <paramref name="local"/> sits inside the solid, in local units. 0 when the point falls
    /// outside the grid, in an empty cell, below the base or at or above the cell top. Otherwise the least of the
    /// distance to the top, the distance to the base and the cell's planar depth.</summary>
    public float DepthAt(Vector3 local)
    {
        if (!TryCell(local.X - Min.X, _cellsX, out int x) || !TryCell(local.Z - Min.Z, _cellsZ, out int z))
            return 0f;

        int cell = z * _cellsX + x;
        float top = _top[cell];
        float baseY = Min.Y;
        if (float.IsNegativeInfinity(top) || local.Y < baseY || local.Y >= top)
            return 0f;

        return MathF.Min(MathF.Min(top - local.Y, local.Y - baseY), _planarDepth[cell]);
    }

    /// <summary>Cells along one axis. An extent within <see cref="EdgeSlackCells"/> of a whole number of cells takes
    /// that number, so float rounding never adds a phantom cell row on the max side.</summary>
    static double AxisCells(float extent, float cellSize) =>
        Math.Max(1d, Math.Ceiling(extent / (double)cellSize - EdgeSlackCells));

    /// <summary>Map an offset from the grid's min edge to a cell index. A coordinate exactly on the max edge
    /// clamps into the last cell. Anything further out than <see cref="EdgeSlackCells"/> is off the grid.</summary>
    bool TryCell(float offset, int cells, out int index)
    {
        double t = offset / (double)CellSize;
        if (!(t >= -EdgeSlackCells && t <= cells + EdgeSlackCells))
        {
            index = -1;
            return false;
        }

        index = ClampedCell(t, cells);
        return true;
    }

    /// <summary>The cell holding <paramref name="t"/>, in cells from the min edge, clamped onto the grid.</summary>
    static int ClampedCell(double t, int cells) => (int)Math.Clamp(Math.Floor(t + EdgeSlackCells), 0d, cells - 1d);

    /// <summary>Fill the cell under a mesh sample and raise its top. Samples come from the mesh, so one that float
    /// drift carries just past the grid edge belongs to the edge cell and is clamped there.</summary>
    void Mark(Vector3 sample)
    {
        int x = ClampedCell((sample.X - Min.X) / (double)CellSize, _cellsX);
        int z = ClampedCell((sample.Z - Min.Z) / (double)CellSize, _cellsZ);
        int cell = z * _cellsX + x;
        if (sample.Y > _top[cell])
            _top[cell] = sample.Y;
    }

    /// <summary>Two-pass chamfer distance transform over the grid padded with one empty ring. Each filled cell
    /// gets its distance, in cells, to the nearest empty cell centre. Planar depth is that distance times the
    /// cell size, less half a cell, floored at 0.</summary>
    void ComputePlanarDepth()
    {
        int w = _cellsX + 2;
        int h = _cellsZ + 2;
        var d = new float[w * h];
        const float straight = 1f;
        float diagonal = MathF.Sqrt(2f);

        for (int z = 0; z < _cellsZ; z++)
        {
            for (int x = 0; x < _cellsX; x++)
            {
                if (!float.IsNegativeInfinity(_top[z * _cellsX + x]))
                    d[(z + 1) * w + x + 1] = float.PositiveInfinity;
            }
        }

        for (int z = 1; z < h - 1; z++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = z * w + x;
                if (d[i] == 0f)
                    continue;
                float best = d[i];
                best = MathF.Min(best, d[i - 1] + straight);
                best = MathF.Min(best, d[i - w] + straight);
                best = MathF.Min(best, d[i - w - 1] + diagonal);
                best = MathF.Min(best, d[i - w + 1] + diagonal);
                d[i] = best;
            }
        }

        for (int z = h - 2; z >= 1; z--)
        {
            for (int x = w - 2; x >= 1; x--)
            {
                int i = z * w + x;
                if (d[i] == 0f)
                    continue;
                float best = d[i];
                best = MathF.Min(best, d[i + 1] + straight);
                best = MathF.Min(best, d[i + w] + straight);
                best = MathF.Min(best, d[i + w + 1] + diagonal);
                best = MathF.Min(best, d[i + w - 1] + diagonal);
                d[i] = best;
            }
        }

        float half = CellSize / 2f;
        for (int z = 0; z < _cellsZ; z++)
        {
            for (int x = 0; x < _cellsX; x++)
            {
                float cells = d[(z + 1) * w + x + 1];
                if (cells > 0f)
                    _planarDepth[z * _cellsX + x] = MathF.Max(0f, cells * CellSize - half);
            }
        }
    }
}

/// <summary>Thrown by <see cref="ClearanceSolid.Build"/> when a mesh at the requested cell size needs a planar grid
/// larger than <see cref="ClearanceSolid.MaxCells"/>. The remedy is a coarser cell size or a smaller piece.</summary>
public sealed class ClearanceTooLargeException : Exception
{
    /// <summary>Build the refusal for a grid of <paramref name="cells"/> cells.</summary>
    public ClearanceTooLargeException(long cells)
        : base($"A clearance grid of {cells} cells exceeds the limit of {ClearanceSolid.MaxCells}. "
            + "Use a coarser cell size or a smaller piece.")
    {
        Cells = cells;
    }

    /// <summary>The planar cell count the refused build needed.</summary>
    public long Cells { get; }
}
