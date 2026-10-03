using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridBlockedSurfacesTests
{
    const int W = 7;
    const int H = 5;
    const float Cell = 0.25f;
    const float OriginX = -1f;
    const float OriginZ = 2f;
    const float Yaw = 0.3f;
    const float YMin = 0f;
    const float YMax = 4f;

    // (2, 1) is standable but a step above its neighbors, (4, 3) has low headroom, (5, 1) is not standable.
    static NavGrid FreshGrid() => NavGrid.FromSurfaces(W, H, Cell, OriginX, OriginZ,
        (x, z) => (x, z) switch
        {
            (2, 1) => new NavSurfaceSample(true, 3f, float.PositiveInfinity),
            (4, 3) => new NavSurfaceSample(true, 1.1f, 1f),
            (5, 1) => new NavSurfaceSample(false, 0f, 0f),
            _ => new NavSurfaceSample(true, 1f + 0.01f * x + 0.003f * z, float.PositiveInfinity),
        },
        stepHeight: 0.5f, agentHeight: 1.8f, yMin: YMin, yMax: YMax, yawRadians: Yaw);

    static (bool[] Blocked, float[] Heights) Decompose(NavGrid grid)
    {
        var blocked = new bool[grid.Width * grid.Height];
        var heights = new float[grid.Width * grid.Height];
        for (int z = 0; z < grid.Height; z++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int i = z * grid.Width + x;
                blocked[i] = grid.ClearanceAt(x, z) == 0;
                heights[i] = grid.SurfaceHeightAt(x, z) ?? 0f;
            }
        }
        return (blocked, heights);
    }

    static void AssertSameGrid(NavGrid expected, NavGrid actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.CellSize), BitConverter.SingleToInt32Bits(actual.CellSize));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.OriginX), BitConverter.SingleToInt32Bits(actual.OriginX));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.OriginZ), BitConverter.SingleToInt32Bits(actual.OriginZ));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.YawRadians), BitConverter.SingleToInt32Bits(actual.YawRadians));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.YMin), BitConverter.SingleToInt32Bits(actual.YMin));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.YMax), BitConverter.SingleToInt32Bits(actual.YMax));
        Assert.Equal(expected.HasSurfaceHeights, actual.HasSurfaceHeights);
        for (int z = 0; z < expected.Height; z++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.ClearanceAt(x, z), actual.ClearanceAt(x, z));
                float? e = expected.SurfaceHeightAt(x, z);
                float? a = actual.SurfaceHeightAt(x, z);
                Assert.Equal(e.HasValue, a.HasValue);
                if (e.HasValue)
                    Assert.Equal(BitConverter.SingleToInt32Bits(e.Value), BitConverter.SingleToInt32Bits(a!.Value));
            }
        }
    }

    [Fact]
    public void RebuildsEveryCellOfASurfaceGrid()
    {
        NavGrid fresh = FreshGrid();
        Assert.Equal(0, fresh.ClearanceAt(2, 1));
        Assert.Equal(0, fresh.ClearanceAt(4, 3));
        Assert.Equal(0, fresh.ClearanceAt(5, 1));
        (bool[] blocked, float[] heights) = Decompose(fresh);

        NavGrid rebuilt = NavGrid.FromBlockedSurfaces(W, H, Cell, OriginX, OriginZ, blocked, heights,
            YMin, YMax, Yaw);

        AssertSameGrid(fresh, rebuilt);
    }

    [Theory]
    [InlineData("blocked-short")]
    [InlineData("blocked-long")]
    [InlineData("heights-short")]
    [InlineData("heights-long")]
    [InlineData("open-nan")]
    [InlineData("open-positive-infinity")]
    [InlineData("open-negative-infinity")]
    [InlineData("blocked-nan")]
    [InlineData("width-zero")]
    [InlineData("height-zero")]
    [InlineData("cell-size-zero")]
    [InlineData("yaw-nan")]
    [InlineData("yaw-infinity")]
    public void RejectsMismatchedSpansAndNonFiniteOpenHeights(string fault)
    {
        int width = 3, height = 2;
        float cellSize = 1f, yaw = 0f;
        var blocked = new bool[width * height];
        var heights = new float[width * height];
        blocked[4] = true;
        switch (fault)
        {
            case "blocked-short": blocked = new bool[width * height - 1]; break;
            case "blocked-long": blocked = new bool[width * height + 1]; break;
            case "heights-short": heights = new float[width * height - 1]; break;
            case "heights-long": heights = new float[width * height + 1]; break;
            case "open-nan": heights[1] = float.NaN; break;
            case "open-positive-infinity": heights[1] = float.PositiveInfinity; break;
            case "open-negative-infinity": heights[1] = float.NegativeInfinity; break;
            case "blocked-nan": heights[4] = float.NaN; break;
            case "width-zero": width = 0; break;
            case "height-zero": height = 0; break;
            case "cell-size-zero": cellSize = 0f; break;
            case "yaw-nan": yaw = float.NaN; break;
            case "yaw-infinity": yaw = float.PositiveInfinity; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }

        NavGrid Build() => NavGrid.FromBlockedSurfaces(width, height, cellSize, 0f, 0f, blocked, heights,
            yawRadians: yaw);

        switch (fault)
        {
            case "blocked-short" or "blocked-long" or "heights-short" or "heights-long":
                Assert.Throws<ArgumentException>(Build);
                break;
            case "blocked-nan":
                NavGrid grid = Build();
                Assert.Null(grid.SurfaceHeightAt(1, 1));
                Assert.Equal(0, grid.ClearanceAt(1, 1));
                break;
            default:
                Assert.Throws<ArgumentOutOfRangeException>(Build);
                break;
        }
    }

    [Fact]
    public void CopiesCallerSpans()
    {
        NavGrid fresh = FreshGrid();
        (bool[] blocked, float[] heights) = Decompose(fresh);
        NavGrid rebuilt = NavGrid.FromBlockedSurfaces(W, H, Cell, OriginX, OriginZ, blocked, heights,
            YMin, YMax, Yaw);

        Array.Fill(blocked, true);
        Array.Fill(heights, 99f);

        AssertSameGrid(fresh, rebuilt);
    }

    [Fact]
    public void AllBlockedGridHasNoSurfaces()
    {
        var blocked = new bool[W * H];
        Array.Fill(blocked, true);
        var heights = new float[W * H];
        Array.Fill(heights, 2f);

        NavGrid grid = NavGrid.FromBlockedSurfaces(W, H, Cell, OriginX, OriginZ, blocked, heights);

        Assert.True(grid.HasSurfaceHeights);
        for (int z = 0; z < H; z++)
        {
            for (int x = 0; x < W; x++)
            {
                Assert.Null(grid.SurfaceHeightAt(x, z));
                Assert.Equal(0, grid.ClearanceAt(x, z));
            }
        }
    }
}
