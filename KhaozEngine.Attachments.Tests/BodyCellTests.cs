using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class BodyCellTests
{
    [Fact]
    public void A_barycentric_sweep_reaches_all_64_grid_indices()
    {
        var indices = new HashSet<int>();
        for (int y = 0; y <= 64; y++)
            for (int x = 0; x <= 64 - y; x++)
                indices.Add(BodyCell.At(0, x / 64d, y / 64d).Index);

        Assert.Equal(Enumerable.Range(0, 64), indices.Order());
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 14)]
    [InlineData(0, 1, 63)]
    [InlineData(0.5, 0, 8)]
    [InlineData(0, 0.5, 48)]
    [InlineData(0.5, 0.5, 54)]
    [InlineData(0.09375, 0.09375, 1)]
    [InlineData(0.0625, 0.0625, 0)]
    [InlineData(-0.000000000001, 0, 0)]
    [InlineData(0, 1.000000000001, 63)]
    [InlineData(0.625000000001, 0.375000000001, 47)]
    public void Grid_edges_and_diagonal_ties_have_defined_cell_ownership(double w1, double w2, int index)
    {
        Assert.Equal(new BodyCell(3, index), BodyCell.At(3, w1, w2));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 64)]
    [InlineData(17, 63, 1151)]
    [InlineData(33_554_430, 63, 2_147_483_583)]
    public void Keys_round_trip_in_triangle_then_cell_order(int triangle, int index, int key)
    {
        var cell = new BodyCell(triangle, index);
        Assert.Equal(key, cell.Key);
        Assert.Equal(cell, BodyCell.FromKey(key));
    }

    [Fact]
    public void Negative_triangle_and_key_arguments_are_refused()
    {
        Assert.Equal(0, BodyCell.At(0, 0, 0).Key);
        Assert.Equal(default, BodyCell.FromKey(0));
        Assert.Throws<ArgumentOutOfRangeException>("triangle", () => BodyCell.At(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("key", () => BodyCell.FromKey(-1));
    }
}
