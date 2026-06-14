using KhaozEngine.Collision;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class SpatialHashGridTests
{
    // Fixed scene reused by the determinism golden-master tests. cellSize = 10.
    //   index 0: (5,5)   r=1  -> cell (0,0)
    //   index 1: (15,5)  r=1  -> cell (1,0)
    //   index 2: (5,15)  r=1  -> cell (0,1)
    //   index 3: (6,6)   r=1  -> cell (0,0)  (same cell as 0, inserted after -> head)
    //   index 4: (-5,-5) r=1  -> cell (-1,-1) (negative floor)
    private static SpatialHashGrid BuildScene()
    {
        var grid = new SpatialHashGrid(10f);
        grid.BeginRebuild(5);
        grid.Add(0, new Vector2(5f, 5f), 1f);
        grid.Add(1, new Vector2(15f, 5f), 1f);
        grid.Add(2, new Vector2(5f, 15f), 1f);
        grid.Add(3, new Vector2(6f, 6f), 1f);
        grid.Add(4, new Vector2(-5f, -5f), 1f);
        return grid;
    }

    [Fact]
    public void EmptyGridReturnsNoCandidates()
    {
        var grid = new SpatialHashGrid(10f);
        Assert.Equal(0, grid.QueryCandidates(new Vector2(0f, 0f), 100f));
    }

    [Fact]
    public void SingleCellQueryReturnsHeadInsertionOrder()
    {
        var grid = BuildScene();

        // searchRadius = max(0,0)+maxRadius(1) = 1 -> only cell (0,0).
        int count = grid.QueryCandidates(new Vector2(5f, 5f), 0f);

        Assert.Equal(2, count);
        // Head-insertion is LIFO: 3 was added after 0 in the same cell, so it comes first.
        Assert.Equal(3, grid.GetQueryIndex(0));
        Assert.Equal(0, grid.GetQueryIndex(1));
    }

    [Fact]
    public void MultiCellQueryWalksYOuterXInnerInExactOrder()
    {
        var grid = BuildScene();

        // searchRadius = 12 + 1 = 13 -> cells x in [-1,1], y in [-1,1].
        // Walk order y=-1..1 (outer), x=-1..1 (inner); chains in head-insertion order.
        int count = grid.QueryCandidates(new Vector2(5f, 5f), 12f);

        Assert.Equal(5, count);
        int[] actual = { grid.GetQueryIndex(0), grid.GetQueryIndex(1), grid.GetQueryIndex(2), grid.GetQueryIndex(3), grid.GetQueryIndex(4) };
        Assert.Equal(new[] { 4, 3, 0, 1, 2 }, actual);
    }

    [Fact]
    public void SortQueryIndicesAscendingSortsInPlace()
    {
        var grid = BuildScene();
        int count = grid.QueryCandidates(new Vector2(5f, 5f), 12f);

        grid.SortQueryIndicesAscending(count);

        int[] actual = { grid.GetQueryIndex(0), grid.GetQueryIndex(1), grid.GetQueryIndex(2), grid.GetQueryIndex(3), grid.GetQueryIndex(4) };
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, actual);
    }

    [Fact]
    public void MaxRadiusExpandsSearchToReachLargerItems()
    {
        var grid = new SpatialHashGrid(10f);
        grid.BeginRebuild(2);
        grid.Add(0, new Vector2(5f, 5f), 1f);
        // A big item three cells away whose radius reaches back toward the query.
        grid.Add(1, new Vector2(35f, 5f), 25f);

        // queryRadius 0 at (5,5): searchRadius = 0 + maxRadius(25) = 25 -> reaches cell (3,0).
        int count = grid.QueryCandidates(new Vector2(5f, 5f), 0f);

        Assert.Equal(2, count);
    }

    [Fact]
    public void ClearEmptiesTheGrid()
    {
        var grid = BuildScene();
        grid.Clear();
        Assert.Equal(0, grid.QueryCandidates(new Vector2(5f, 5f), 100f));
    }

    [Fact]
    public void RebuildReplacesPreviousContents()
    {
        var grid = BuildScene();
        grid.QueryCandidates(new Vector2(5f, 5f), 100f);

        grid.BeginRebuild(8); // capacity must exceed the largest index added (7)
        grid.Add(7, new Vector2(5f, 5f), 1f);

        int count = grid.QueryCandidates(new Vector2(5f, 5f), 0f);
        Assert.Equal(1, count);
        Assert.Equal(7, grid.GetQueryIndex(0));
    }

    [Fact]
    public void QueryGrowsResultBufferBeyondInitialCapacity()
    {
        var grid = new SpatialHashGrid(1f);
        grid.BeginRebuild(200);
        for (int i = 0; i < 200; i++)
        {
            // All in cell (0,0) so a single query returns all 200, forcing buffer growth.
            grid.Add(i, new Vector2(0.5f, 0.5f), 0f);
        }

        int count = grid.QueryCandidates(new Vector2(0.5f, 0.5f), 0f);
        Assert.Equal(200, count);
    }
}
