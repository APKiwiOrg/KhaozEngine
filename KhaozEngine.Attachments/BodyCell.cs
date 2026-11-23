using System;

namespace KhaozEngine.Attachments;

/// <summary>A cell in a body triangle's barycentric lift grid.</summary>
/// <param name="Triangle">The triangle index flattened over body region map parts in order.</param>
/// <param name="Index">The cell index within the triangle, from 0 to 63.</param>
public readonly record struct BodyCell(int Triangle, int Index)
{
    /// <summary>The number of subdivisions along each triangle edge.</summary>
    public const int Level = 8;

    /// <summary>The number of cells in each triangle.</summary>
    public const int CellsPerTriangle = Level * Level;

    /// <summary>The checked integer key that orders cells by triangle, then by cell index.</summary>
    public int Key => checked(Triangle * CellsPerTriangle + Index);

    /// <summary>Recover the triangle and cell index from a nonnegative key.</summary>
    public static BodyCell FromKey(int key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(key);
        return new BodyCell(key / CellsPerTriangle, key % CellsPerTriangle);
    }

    /// <summary>
    /// Locate the cell containing a point with barycentric weights <paramref name="w1"/> and
    /// <paramref name="w2"/> for the second and third corners in index order. Finite weights are clamped
    /// to the closed triangle, keeping the third corner's weight on the diagonal edge.
    /// </summary>
    /// <remarks>
    /// The column is the floor of <c>w1 * 8</c> and the row the floor of <c>w2 * 8</c>, so a point on an interior grid
    /// line belongs to the cell on its higher side. A point on a grid square's own diagonal belongs to the square's
    /// lower cell. A point on the triangle's edge opposite the first corner belongs to the cell along that edge.
    /// Weights must be finite. A caller passes the weights of a closest point, which always are.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The triangle is negative or a weight is NaN or infinite.</exception>
    public static BodyCell At(int triangle, double w1, double w2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(triangle);
        if (!double.IsFinite(w1)) throw new ArgumentOutOfRangeException(nameof(w1), w1, "The weight must be finite.");
        if (!double.IsFinite(w2)) throw new ArgumentOutOfRangeException(nameof(w2), w2, "The weight must be finite.");

        w2 = Math.Clamp(w2, 0, 1);
        w1 = Math.Clamp(w1, 0, 1 - w2);
        double x = w1 * Level;
        double y = w2 * Level;
        int i = Math.Min((int)x, Level - 1);
        int j = Math.Min((int)y, Level - 1);
        if (i + j > Level - 1)
            i = Level - 1 - j;

        bool upper = (x - i) + (y - j) > 1 && i + j <= Level - 2;
        return new BodyCell(triangle, j * (2 * Level - j) + 2 * i + (upper ? 1 : 0));
    }
}
