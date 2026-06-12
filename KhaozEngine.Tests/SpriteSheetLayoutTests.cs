using System;
using KhaozEngine.Sprites;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class SpriteSheetLayoutTests
{
    [Fact]
    public void FromGrid_derives_frame_size_from_sheet_and_counts()
    {
        var layout = SpriteSheetLayout.FromGrid(sheetWidth: 80, sheetHeight: 160, rows: 8, columns: 4);

        Assert.Equal(8, layout.Rows);
        Assert.Equal(4, layout.Columns);
        Assert.Equal(20, layout.FrameWidth);
        Assert.Equal(20, layout.FrameHeight);
    }

    [Fact]
    public void FromFrameSize_derives_row_and_column_counts()
    {
        var layout = SpriteSheetLayout.FromFrameSize(sheetWidth: 80, sheetHeight: 160, frameWidth: 20, frameHeight: 20);

        Assert.Equal(8, layout.Rows);
        Assert.Equal(4, layout.Columns);
    }

    [Fact]
    public void GetFrame_returns_source_rectangle_for_cell()
    {
        var layout = SpriteSheetLayout.FromGrid(80, 160, rows: 8, columns: 4);

        Assert.Equal(new Rectangle(0, 0, 20, 20), layout.GetFrame(0, 0));
        // row 1, column 2 -> x = 2*20, y = 1*20
        Assert.Equal(new Rectangle(40, 20, 20, 20), layout.GetFrame(1, 2));
        Assert.Equal(new Rectangle(60, 140, 20, 20), layout.GetFrame(7, 3));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(8, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    public void GetFrame_out_of_range_throws(int row, int column)
    {
        var layout = SpriteSheetLayout.FromGrid(80, 160, rows: 8, columns: 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetFrame(row, column));
    }

    [Fact]
    public void FromGrid_rejects_non_positive_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteSheetLayout.FromGrid(80, 160, rows: 0, columns: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteSheetLayout.FromGrid(80, 160, rows: 8, columns: 0));
    }
}
