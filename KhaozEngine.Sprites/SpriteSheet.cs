using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Sprites;

/// <summary>
/// A <see cref="Texture2D"/> laid out as a uniform grid of frames. Pairs a texture with a
/// <see cref="SpriteSheetLayout"/> and exposes the source rectangle (or <see cref="SpriteFrame"/>)
/// for any cell. Use <see cref="FromFrameSize"/> when you know the per-frame pixel size, or
/// <see cref="FromGrid"/> when you know the row/column counts.
/// </summary>
public sealed class SpriteSheet
{
    /// <summary>The backing texture.</summary>
    public Texture2D Texture { get; }

    /// <summary>The grid geometry.</summary>
    public SpriteSheetLayout Layout { get; }

    /// <summary>Number of rows in the grid.</summary>
    public int Rows => Layout.Rows;
    /// <summary>Number of columns in the grid.</summary>
    public int Columns => Layout.Columns;
    /// <summary>Width of a single frame, in pixels.</summary>
    public int FrameWidth => Layout.FrameWidth;
    /// <summary>Height of a single frame, in pixels.</summary>
    public int FrameHeight => Layout.FrameHeight;

    /// <summary>Wraps a texture with an explicit layout.</summary>
    public SpriteSheet(Texture2D texture, SpriteSheetLayout layout)
    {
        Texture = texture;
        Layout = layout;
    }

    /// <summary>Builds a sheet from a texture and the per-frame pixel size.</summary>
    public static SpriteSheet FromFrameSize(Texture2D texture, int frameWidth, int frameHeight) =>
        new(texture, SpriteSheetLayout.FromFrameSize(texture.Width, texture.Height, frameWidth, frameHeight));

    /// <summary>Builds a sheet from a texture and the grid row/column counts.</summary>
    public static SpriteSheet FromGrid(Texture2D texture, int rows, int columns) =>
        new(texture, SpriteSheetLayout.FromGrid(texture.Width, texture.Height, rows, columns));

    /// <summary>The source rectangle of the frame at (<paramref name="row"/>, <paramref name="column"/>).</summary>
    public Rectangle GetFrame(int row, int column) => Layout.GetFrame(row, column);

    /// <summary>The drawable <see cref="SpriteFrame"/> at (<paramref name="row"/>, <paramref name="column"/>).</summary>
    public SpriteFrame Frame(int row, int column) => new(Texture, Layout.GetFrame(row, column));
}
