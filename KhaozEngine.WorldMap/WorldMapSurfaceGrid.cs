using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap;

/// <summary>A source-lattice surface grid with samples at cell centres and rows ordered south to north.</summary>
public sealed class WorldMapSurfaceGrid
{
    /// <summary>Allocates a grid whose cells start void and dry.</summary>
    /// <param name="origin">The south-west corner of cell (0, 0), in map metres.</param>
    /// <param name="cellSize">A finite, positive cell edge length in metres.</param>
    /// <param name="width">The non-negative number of columns.</param>
    /// <param name="height">The non-negative number of rows.</param>
    public WorldMapSurfaceGrid(Vector2 origin, float cellSize, int width, int height)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be finite and positive.");
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        Origin = origin;
        CellSize = cellSize;
        Width = width;
        Height = height;
        int count = checked(width * height);
        Ground = new Color[count];
        SurfaceHeight = new float[count];
        WaterSurface = new float[count];
        Array.Fill(SurfaceHeight, float.NaN);
        Array.Fill(WaterSurface, float.NaN);
    }

    /// <summary>The south-west corner of cell (0, 0), in map metres.</summary>
    public Vector2 Origin { get; }

    /// <summary>The cell edge length in metres.</summary>
    public float CellSize { get; }

    /// <summary>The number of eastward columns.</summary>
    public int Width { get; }

    /// <summary>The number of northward rows.</summary>
    public int Height { get; }

    /// <summary>Row-major ground colours. <see cref="Color.Transparent"/> marks a void cell.</summary>
    public Color[] Ground { get; }

    /// <summary>Row-major surface heights in metres. Void cells have <see cref="float.NaN"/> heights.</summary>
    public float[] SurfaceHeight { get; }

    /// <summary>Row-major water heights in metres, or <see cref="float.NaN"/> where dry.</summary>
    public float[] WaterSurface { get; }

    /// <summary>Returns row * Width + column. Row zero is the south row. Out-of-grid coordinates are refused.</summary>
    public int Index(int column, int row)
    {
        if (column < 0 || column >= Width) throw new ArgumentOutOfRangeException(nameof(column));
        if (row < 0 || row >= Height) throw new ArgumentOutOfRangeException(nameof(row));
        return row * Width + column;
    }
}
