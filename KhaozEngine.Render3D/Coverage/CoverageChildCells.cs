using System;

namespace KhaozEngine.Render3D;

/// <summary>Integer barycentric weights over <see cref="CoverageChildCells.Denominator"/>, in the source triangle's
/// corner order A, B, C.</summary>
public readonly record struct CoverageLatticePoint(int A, int B, int C);

/// <summary>One cell of a source triangle's level lattice. Corner order and winding match the source triangle.</summary>
public readonly record struct CoverageChildCell(CoverageLatticePoint A, CoverageLatticePoint B, CoverageLatticePoint C);

/// <summary>
/// The only definition of child cell numbering. A source triangle refined at level <c>n</c> splits into <c>n * n</c>
/// cells on a lattice of step <c>12 / n</c>, where lattice point <c>(x, y)</c> has weights
/// <c>(12 - (x + y) * step, x * step, y * step)</c>. Cells count up through <c>x</c> from 0, then <c>y</c> from 0 to
/// <c>n - 1 - x</c>. Each step yields the up cell <c>((x, y), (x + 1, y), (x, y + 1))</c>, then, when
/// <c>x + y &lt; n - 1</c>, the down cell <c>((x + 1, y), (x + 1, y + 1), (x, y + 1))</c>.
/// </summary>
public static class CoverageChildCells
{
    /// <summary>Common denominator of every lattice weight. Divisible by every level from 1 to 4.</summary>
    public const int Denominator = 12;

    /// <summary>Number of cells at <paramref name="level"/>, which is <c>level * level</c>.</summary>
    public static int CellCount(int level)
    {
        ValidateLevel(level);
        return level * level;
    }

    /// <summary>Corners of <paramref name="cell"/> in the level lattice, in <c>[0, level * level)</c>.</summary>
    public static CoverageChildCell Get(int level, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, CellCount(level));
        int step = Denominator / level, index = cell;
        for (int x = 0; ; x++)
        {
            // Column x holds level - x up cells interleaved with level - x - 1 down cells.
            int column = 2 * (level - x) - 1;
            if (index >= column)
            {
                index -= column;
                continue;
            }
            int y = index / 2;
            return index % 2 == 0
                ? new(At(x, y), At(x + 1, y), At(x, y + 1))
                : new(At(x + 1, y), At(x + 1, y + 1), At(x, y + 1));
        }
        CoverageLatticePoint At(int x, int y) => new(Denominator - (x + y) * step, x * step, y * step);
    }

    static void ValidateLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, HostCoverageMap.MinRefinementLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, HostCoverageMap.MaxRefinementLevel);
    }
}
