using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests;

public class GridRayTests
{
    // Helper: a blocker predicate over an explicit set of blocking cells.
    private static System.Func<int, int, bool> Blockers(params (int X, int Y)[] cells)
    {
        var set = new HashSet<(int, int)>(cells);
        return (x, y) => set.Contains((x, y));
    }

    [Fact]
    public void StraightLineWithNoBlockersIsClear()
    {
        // Horizontal segment across several empty cells.
        Assert.True(GridRay.IsClear(new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers()));
    }

    [Fact]
    public void WallCellBetweenEndpointsBlocks()
    {
        // from cell (0,0) to cell (9,0); wall at cell (4,0) sits squarely on the line.
        Assert.False(GridRay.IsClear(new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers((4, 0))));
    }

    [Theory]
    [InlineData(5f, 55f, 1, 0)] // X-first corner-adjacent cell.
    [InlineData(2f, 38f, 2, 2)] // Thin wall on the diagonal interior.
    public void DiagonalBlockersAreNotSkipped(float start, float end, int blockerX, int blockerY)
    {
        var from = new Vector2(start, start);
        var to = new Vector2(end, end);
        Assert.False(GridRay.IsClear(from, to, 10f, Blockers((blockerX, blockerY))));
    }

    [Fact]
    public void ZeroLengthSegmentIsClear()
    {
        var p = new Vector2(5f, 5f);
        Assert.True(GridRay.IsClear(p, p, 10f, Blockers((0, 0))));
    }

    [Fact]
    public void BlockerOnlyOnEndpointCellIsClearByDefault()
    {
        // Wall in the destination cell (9,0): endpoint cells are excluded by default, so the line is clear.
        Assert.True(GridRay.IsClear(new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers((9, 0))));
        // Wall in the origin cell (0,0): likewise clear.
        Assert.True(GridRay.IsClear(new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers((0, 0))));
    }

    [Fact]
    public void BlockerOnEndpointCellBlocksWhenEndpointsIncluded()
    {
        Assert.False(GridRay.IsClear(
            new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers((9, 0)), includeEndpointCells: true));
        Assert.False(GridRay.IsClear(
            new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, Blockers((0, 0)), includeEndpointCells: true));
    }

    [Fact]
    public void NegativeCoordinatesUseFlooringCellMapping()
    {
        // from (-5,-5) -> cell (-1,-1), to (-95,-5) -> cell (-10,-1). Wall at (-5,-1) sits on the line.
        Assert.False(GridRay.IsClear(new Vector2(-5f, -5f), new Vector2(-95f, -5f), 10f, Blockers((-5, -1))));
    }

    [Fact]
    public void TraceVisitsContiguousCellsIncludingEndpoints()
    {
        var visited = new List<(int, int)>();
        bool completed = GridRay.Trace(new Vector2(5f, 5f), new Vector2(35f, 5f), 10f, (x, y) =>
        {
            visited.Add((x, y));
            return true;
        });

        Assert.True(completed);
        Assert.Equal(new[] { (0, 0), (1, 0), (2, 0), (3, 0) }, visited);
    }

    [Fact]
    public void TraceCellsAreFourConnected()
    {
        // Every consecutive pair of visited cells differs by exactly one in a single axis (no diagonal jumps).
        var visited = new List<(int X, int Y)>();
        GridRay.Trace(new Vector2(3f, 3f), new Vector2(77f, 51f), 10f, (x, y) =>
        {
            visited.Add((x, y));
            return true;
        });

        for (int i = 1; i < visited.Count; i++)
        {
            int dx = System.Math.Abs(visited[i].X - visited[i - 1].X);
            int dy = System.Math.Abs(visited[i].Y - visited[i - 1].Y);
            Assert.Equal(1, dx + dy);
        }

        // First and last cells are the endpoint cells.
        Assert.Equal((0, 0), visited[0]);
        Assert.Equal((7, 5), visited[^1]);
    }

    [Fact]
    public void TraceStopsEarlyWhenVisitReturnsFalse()
    {
        int count = 0;
        bool completed = GridRay.Trace(new Vector2(5f, 5f), new Vector2(95f, 5f), 10f, (x, y) =>
        {
            count++;
            return x < 2; // stop after entering cell (2,0)
        });

        Assert.False(completed);
        Assert.Equal(3, count); // visited (0,0),(1,0),(2,0) then stopped
    }
}
