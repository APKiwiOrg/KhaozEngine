using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The public cell numbering of a source triangle's refinement lattice. Child role overrides address cells by
/// this index, so the numbering and corners are the contract.</summary>
public class CoverageChildCellTests
{
    [Fact]
    public void Child_cells_number_the_level_lattice_once()
    {
        const int whole = 12 * 12; // twice the source triangle's area in (B, C) weight space
        for (int level = 1; level <= 4; level++)
        {
            Assert.Equal(level * level, CoverageChildCells.CellCount(level));
            int step = 12 / level, area = 0;
            var seen = new HashSet<(CoverageLatticePoint, CoverageLatticePoint, CoverageLatticePoint)>();
            for (int cell = 0; cell < CoverageChildCells.CellCount(level); cell++)
            {
                CoverageChildCell child = CoverageChildCells.Get(level, cell);
                foreach (CoverageLatticePoint corner in new[] { child.A, child.B, child.C })
                {
                    Assert.Equal(CoverageChildCells.Denominator, corner.A + corner.B + corner.C);
                    Assert.True(corner.A >= 0 && corner.B >= 0 && corner.C >= 0);
                    Assert.True(corner.B % step == 0 && corner.C % step == 0);
                }
                int signed = (child.B.B - child.A.B) * (child.C.C - child.A.C) - (child.B.C - child.A.C) * (child.C.B - child.A.B);
                // Distinct unit cells of the level lattice inside the triangle never overlap, so equal unit areas that
                // sum to the whole prove the cells tile it once.
                Assert.Equal(whole / (level * level), signed);
                Assert.True(seen.Add(Canonical(child)));
                area += signed;
            }
            Assert.Equal(whole, area);
        }

        Assert.Equal(new CoverageChildCell(new(12, 0, 0), new(6, 6, 0), new(6, 0, 6)), CoverageChildCells.Get(2, 0));
        Assert.Equal(new CoverageChildCell(new(6, 6, 0), new(0, 6, 6), new(6, 0, 6)), CoverageChildCells.Get(2, 1));
        Assert.Equal(new CoverageChildCell(new(6, 0, 6), new(0, 6, 6), new(0, 0, 12)), CoverageChildCells.Get(2, 2));
        Assert.Equal(new CoverageChildCell(new(6, 6, 0), new(0, 12, 0), new(0, 6, 6)), CoverageChildCells.Get(2, 3));
        Assert.Equal(new CoverageChildCell(new(9, 0, 3), new(6, 3, 3), new(6, 0, 6)), CoverageChildCells.Get(4, 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.CellCount(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.CellCount(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.Get(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.Get(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.Get(3, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageChildCells.Get(3, 9));
        Assert.Equal(new CoverageChildCell(new(4, 8, 0), new(0, 12, 0), new(0, 8, 4)), CoverageChildCells.Get(3, 8));
    }

    static (CoverageLatticePoint, CoverageLatticePoint, CoverageLatticePoint) Canonical(CoverageChildCell cell)
    {
        var corners = new[] { cell.A, cell.B, cell.C };
        Array.Sort(corners, (left, right) => (left.A, left.B, left.C).CompareTo((right.A, right.B, right.C)));
        return (corners[0], corners[1], corners[2]);
    }
}
