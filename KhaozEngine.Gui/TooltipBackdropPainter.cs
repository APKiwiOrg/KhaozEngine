using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>Tile the whole source texture within the border, cropping partial edge tiles.</summary>
internal static class TooltipBackdropPainter
{
    internal static void Draw<TSink>(ref TSink sink, in Rect interior, in TooltipBackdrop backdrop, float opacity)
        where TSink : ITooltipSink
    {
        float size = backdrop.TileSize;
        if (!float.IsFinite(size) || size <= 0f)
            throw new ArgumentOutOfRangeException(nameof(backdrop.TileSize), "Tile size must be finite and positive.");
        if (interior.Width <= 0f || interior.Height <= 0f) return;

        Vector4 tint = GuiDraw.WithOpacity(backdrop.Tint, opacity);
        for (float y = 0f; y < interior.Height; y += size)
        {
            float height = MathF.Min(size, interior.Height - y);
            for (float x = 0f; x < interior.Width; x += size)
            {
                float width = MathF.Min(size, interior.Width - x);
                var destination = new Rect(interior.X + x, interior.Y + y, width, height);
                sink.Sprite(backdrop.Texture, destination, new Vector4(0f, 0f, width / size, height / size), tint);
            }
        }
    }
}
