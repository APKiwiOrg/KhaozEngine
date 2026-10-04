using System;
using System.Numerics;

namespace KhaozEngine.Sharding;

/// <summary>
/// The uniform world grid a <see cref="ShardHost"/> partitions space into: a cell edge length and the world point
/// where cell (0, 0) begins. The one owner of the world-to-cell and cell-to-world conversion, so cell keying,
/// handoff, border ghosting and the island frame all agree on where a cell lies.
/// <para>A position belongs to cell <c>floor((p - Origin) / CellSize)</c> on each axis, so a point on a cell's lower
/// edge belongs to that cell and positions below the origin floor downward. The default origin, the world origin,
/// keeps <see cref="CellCoord.FromWorld"/>'s grid exactly. Move the origin when an authored world extends below an
/// axis origin and must not be split there.</para>
/// </summary>
public readonly struct CellGrid
{
    /// <param name="cellSize">Cell edge length in world units. Must be positive and finite.</param>
    /// <param name="origin">The world point where cell (0, 0) begins. Both components must be finite.</param>
    public CellGrid(float cellSize, Vector2 origin)
    {
        if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive and finite.");
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Cell grid origin must be finite.");
        CellSize = cellSize;
        Origin = origin;
    }

    /// <summary>Cell edge length in world units.</summary>
    public float CellSize { get; }

    /// <summary>The world point where cell (0, 0) begins.</summary>
    public Vector2 Origin { get; }

    /// <summary>The cell containing world position (<paramref name="worldX"/>, <paramref name="worldY"/>).</summary>
    public CellCoord CoordFor(float worldX, float worldY) =>
        CellCoord.FromWorld(worldX - Origin.X, worldY - Origin.Y, CellSize);

    /// <summary>The world-space corners of the cell at <paramref name="coord"/>: its lower edge, which belongs to it,
    /// and its upper edge, which belongs to the next cell.</summary>
    public (Vector2 Min, Vector2 Max) BoundsOf(CellCoord coord)
    {
        var min = new Vector2(Origin.X + coord.X * CellSize, Origin.Y + coord.Y * CellSize);
        return (min, new Vector2(min.X + CellSize, min.Y + CellSize));
    }

    /// <summary>The world-space centre of the cell at <paramref name="coord"/>.</summary>
    public Vector2 CenterOf(CellCoord coord) =>
        new(Origin.X + (coord.X + 0.5f) * CellSize, Origin.Y + (coord.Y + 0.5f) * CellSize);
}
