using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>Tile the whole source texture within the border, cropping partial edge tiles.</summary>
internal static class TooltipBackdropPainter
{
    const int MaxTileCount = 4096;

    internal static void Draw<TSink>(ref TSink sink, in Rect interior, in TooltipBackdrop backdrop, float opacity)
        where TSink : ITooltipSink
    {
        float size = backdrop.TileSize;
        if (!float.IsFinite(size) || size <= 0f)
            throw new ArgumentOutOfRangeException(nameof(backdrop.TileSize), "Tile size must be finite and positive.");
        if (!float.IsFinite(interior.Width) || !float.IsFinite(interior.Height))
            throw new ArgumentOutOfRangeException(nameof(interior), "Interior dimensions must be finite.");
        if (interior.Width <= 0f || interior.Height <= 0f) return;

        // Check the entire budget in double precision before converting counts or emitting any tiles.
        double columnCount = Math.Ceiling((double)interior.Width / size);
        double rowCount = Math.Ceiling((double)interior.Height / size);
        if (columnCount * rowCount > MaxTileCount)
            throw new ArgumentOutOfRangeException(nameof(backdrop.TileSize),
                "The backdrop must not require more than 4096 tiles.");
        int columns = (int)columnCount;
        int rows = (int)rowCount;

        Vector4 tint = GuiDraw.WithOpacity(backdrop.Tint, opacity);
        for (int row = 0; row < rows; row++)
        {
            double y = row * (double)size;
            float height = (float)Math.Min(size, interior.Height - y);
            for (int column = 0; column < columns; column++)
            {
                double x = column * (double)size;
                float width = (float)Math.Min(size, interior.Width - x);
                var destination = new Rect(interior.X + (float)x, interior.Y + (float)y, width, height);
                sink.Sprite(backdrop.Texture, destination, new Vector4(0f, 0f, width / size, height / size), tint);
            }
        }
    }
}
