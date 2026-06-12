using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Sprites;

/// <summary>
/// The grid geometry of a sprite sheet: how a sheet of a given pixel size divides into a
/// <see cref="Rows"/> x <see cref="Columns"/> grid of equal-sized frames, and the source
/// <see cref="Rectangle"/> for any cell. Pure math, no <c>Texture2D</c>, so it is headless-testable;
/// <see cref="SpriteSheet"/> pairs one of these with a texture.
/// </summary>
public sealed class SpriteSheetLayout
{
    /// <summary>Width of a single frame, in pixels.</summary>
    public int FrameWidth { get; }
    /// <summary>Height of a single frame, in pixels.</summary>
    public int FrameHeight { get; }
    /// <summary>Number of rows (frames stacked vertically).</summary>
    public int Rows { get; }
    /// <summary>Number of columns (frames across).</summary>
    public int Columns { get; }

    private SpriteSheetLayout(int frameWidth, int frameHeight, int rows, int columns)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Rows = rows;
        Columns = columns;
    }

    /// <summary>Builds a layout from the sheet size and explicit per-frame size.</summary>
    public static SpriteSheetLayout FromFrameSize(int sheetWidth, int sheetHeight, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0) throw new ArgumentOutOfRangeException(nameof(frameWidth));
        if (frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(frameHeight));
        if (sheetWidth < frameWidth) throw new ArgumentOutOfRangeException(nameof(sheetWidth));
        if (sheetHeight < frameHeight) throw new ArgumentOutOfRangeException(nameof(sheetHeight));
        return new SpriteSheetLayout(frameWidth, frameHeight, sheetHeight / frameHeight, sheetWidth / frameWidth);
    }

    /// <summary>Builds a layout from the sheet size and an explicit row/column count.</summary>
    public static SpriteSheetLayout FromGrid(int sheetWidth, int sheetHeight, int rows, int columns)
    {
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (sheetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sheetWidth));
        if (sheetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sheetHeight));
        return new SpriteSheetLayout(sheetWidth / columns, sheetHeight / rows, rows, columns);
    }

    /// <summary>The source rectangle of the frame at (<paramref name="row"/>, <paramref name="column"/>).</summary>
    public Rectangle GetFrame(int row, int column)
    {
        if (row < 0 || row >= Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 0 || column >= Columns) throw new ArgumentOutOfRangeException(nameof(column));
        return new Rectangle(column * FrameWidth, row * FrameHeight, FrameWidth, FrameHeight);
    }
}
