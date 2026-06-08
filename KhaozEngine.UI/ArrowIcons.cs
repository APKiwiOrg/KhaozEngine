using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.UI;

/// <summary>
/// Static helpers that draw arrow primitives for <see cref="MiniButton"/> icons.
/// Arrows are rendered as stacked horizontal strips to form filled triangles.
/// </summary>
public static class ArrowIcons
{
    /// <summary>
    /// Draws an upward-pointing filled triangle centered in the given bounds.
    /// Tip at top, wide base at bottom.
    /// </summary>
    public static void DrawUpArrow(SpriteBatch spriteBatch, PrimitiveRenderer renderer,
        Rectangle bounds, Color color)
    {
        int pad = bounds.Width / 4;
        int top = bounds.Y + pad;
        int bottom = bounds.Bottom - pad;
        int height = bottom - top;
        int baseWidth = bounds.Width - pad * 2;

        if (height <= 0 || baseWidth <= 0) return;

        int cx = bounds.X + bounds.Width / 2;

        for (int row = 0; row < height; row++)
        {
            // row 0 = top (narrow tip), row max = bottom (wide base)
            float t = height > 1 ? (float)row / (height - 1) : 1f;
            int w = (int)(baseWidth * t) + 1;
            int x = cx - w / 2;
            renderer.DrawFilledRect(spriteBatch,
                new Rectangle(x, top + row, w, 1), color);
        }
    }

    /// <summary>
    /// Draws a downward-pointing filled triangle centered in the given bounds.
    /// Wide base at top, tip at bottom.
    /// </summary>
    public static void DrawDownArrow(SpriteBatch spriteBatch, PrimitiveRenderer renderer,
        Rectangle bounds, Color color)
    {
        int pad = bounds.Width / 4;
        int top = bounds.Y + pad;
        int bottom = bounds.Bottom - pad;
        int height = bottom - top;
        int baseWidth = bounds.Width - pad * 2;

        if (height <= 0 || baseWidth <= 0) return;

        int cx = bounds.X + bounds.Width / 2;

        for (int row = 0; row < height; row++)
        {
            // row 0 = top (wide base), row max = bottom (narrow tip)
            float t = height > 1 ? (float)row / (height - 1) : 1f;
            int w = (int)(baseWidth * (1f - t)) + 1;
            int x = cx - w / 2;
            renderer.DrawFilledRect(spriteBatch,
                new Rectangle(x, top + row, w, 1), color);
        }
    }
}
