using System;

namespace KhaozEngine.Sharding;

/// <summary>
/// Integer coordinate of a cell in the uniform world grid. <see cref="FromWorld"/> floors a continuous world
/// position into the cell that contains it, using the same cell math as
/// <see cref="KhaozEngine.Replication.InterestGrid"/> (<c>floor(v / cellSize)</c>), so a point on a cell's lower
/// edge belongs to that cell and negative coordinates floor downward (not toward zero). A value type with value
/// equality, suitable as a dictionary key for the <see cref="ShardHost"/> cell map.
/// </summary>
public readonly struct CellCoord : IEquatable<CellCoord>
{
    public CellCoord(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Cell column (floored world X / cell size).</summary>
    public int X { get; }

    /// <summary>Cell row (floored world Y / cell size).</summary>
    public int Y { get; }

    /// <summary>
    /// The cell containing world position (<paramref name="worldX"/>, <paramref name="worldY"/>) for a grid of
    /// the given <paramref name="cellSize"/>. Mirrors <see cref="KhaozEngine.Replication.InterestGrid"/>'s
    /// <c>floor(v / cellSize)</c>.
    /// </summary>
    /// <param name="worldX">World X position.</param>
    /// <param name="worldY">World Y position.</param>
    /// <param name="cellSize">Cell edge length in world units. Must be &gt; 0.</param>
    public static CellCoord FromWorld(float worldX, float worldY, float cellSize)
    {
        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        return new CellCoord((int)MathF.Floor(worldX / cellSize), (int)MathF.Floor(worldY / cellSize));
    }

    public bool Equals(CellCoord other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is CellCoord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public static bool operator ==(CellCoord left, CellCoord right) => left.Equals(right);

    public static bool operator !=(CellCoord left, CellCoord right) => !left.Equals(right);

    public override string ToString() => $"({X}, {Y})";
}
