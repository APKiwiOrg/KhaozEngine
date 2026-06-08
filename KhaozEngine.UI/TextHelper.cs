using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.UI;

/// <summary>
/// Centralized text rendering utility. ALL text in the game should be drawn
/// through these methods to guarantee integer pixel positions (no sub-pixel blur).
///
/// NEVER call SpriteBatch.DrawString directly  -- always use TextHelper.
/// </summary>
public static class TextHelper
{
    /// <summary>
    /// Draws text at the given position, snapped to integer pixels.
    /// </summary>
    public static void Draw(SpriteBatch spriteBatch, SpriteFont font, string text, int x, int y, Color color)
    {
        spriteBatch.DrawString(font, text, new Vector2(x, y), color);
    }

    /// <summary>
    /// Draws text at the given position, snapped to integer pixels.
    /// Accepts float positions and floors them to prevent sub-pixel blur.
    /// </summary>
    public static void Draw(SpriteBatch spriteBatch, SpriteFont font, string text, float x, float y, Color color)
    {
        spriteBatch.DrawString(font, text, new Vector2((int)x, (int)y), color);
    }

    /// <summary>
    /// Draws text with alpha modulation, snapped to integer pixels.
    /// </summary>
    public static void Draw(SpriteBatch spriteBatch, SpriteFont font, string text, float x, float y, Color color, float alpha)
    {
        spriteBatch.DrawString(font, text, new Vector2((int)x, (int)y), color * alpha);
    }

    /// <summary>
    /// Draws text horizontally centered within a given width, at the specified Y.
    /// </summary>
    public static void DrawCentered(SpriteBatch spriteBatch, SpriteFont font, string text, int centerX, int y, Color color)
    {
        Vector2 size = font.MeasureString(text);
        int x = centerX - (int)(size.X / 2f);
        spriteBatch.DrawString(font, text, new Vector2(x, y), color);
    }

    /// <summary>
    /// Draws text horizontally centered with alpha, snapped to integer pixels.
    /// </summary>
    public static void DrawCentered(SpriteBatch spriteBatch, SpriteFont font, string text, int centerX, int y, Color color, float alpha)
    {
        Vector2 size = font.MeasureString(text);
        int x = centerX - (int)(size.X / 2f);
        spriteBatch.DrawString(font, text, new Vector2(x, y), color * alpha);
    }

    /// <summary>
    /// Draws text right-aligned at the given right edge X, snapped to integer pixels.
    /// </summary>
    public static void DrawRight(SpriteBatch spriteBatch, SpriteFont font, string text, int rightX, int y, Color color)
    {
        Vector2 size = font.MeasureString(text);
        int x = rightX - (int)size.X;
        spriteBatch.DrawString(font, text, new Vector2(x, y), color);
    }

    /// <summary>
    /// Draws text right-aligned with alpha, snapped to integer pixels.
    /// </summary>
    public static void DrawRight(SpriteBatch spriteBatch, SpriteFont font, string text, int rightX, int y, Color color, float alpha)
    {
        Vector2 size = font.MeasureString(text);
        int x = rightX - (int)size.X;
        spriteBatch.DrawString(font, text, new Vector2(x, y), color * alpha);
    }

    /// <summary>
    /// Draws text centered both horizontally and vertically within a rectangle.
    /// </summary>
    public static void DrawCenteredInRect(SpriteBatch spriteBatch, SpriteFont font, string text, Rectangle rect, Color color)
    {
        Vector2 size = font.MeasureString(text);
        int x = rect.X + rect.Width / 2 - (int)(size.X / 2f);
        int y = rect.Y + rect.Height / 2 - (int)(size.Y / 2f);
        spriteBatch.DrawString(font, text, new Vector2(x, y), color);
    }

    /// <summary>
    /// Draws text centered both horizontally and vertically within a rectangle, with alpha.
    /// </summary>
    public static void DrawCenteredInRect(SpriteBatch spriteBatch, SpriteFont font, string text, Rectangle rect, Color color, float alpha)
    {
        Vector2 size = font.MeasureString(text);
        int x = rect.X + rect.Width / 2 - (int)(size.X / 2f);
        int y = rect.Y + rect.Height / 2 - (int)(size.Y / 2f);
        spriteBatch.DrawString(font, text, new Vector2(x, y), color * alpha);
    }

    /// <summary>
    /// Word-wraps text to fit within maxWidth and draws each line centered.
    /// Returns the total height of the drawn text block.
    /// </summary>
    public static int DrawWrappedCentered(SpriteBatch spriteBatch, SpriteFont font, string text,
        int centerX, int y, int maxWidth, Color color, float alpha)
    {
        List<string> lines = WrapText(font, text, maxWidth);
        int lineHeight = font.LineSpacing;
        int currentY = y;

        foreach (string line in lines)
        {
            Vector2 size = font.MeasureString(line);
            int x = centerX - (int)(size.X / 2f);
            spriteBatch.DrawString(font, line, new Vector2(x, currentY), color * alpha);
            currentY += lineHeight;
        }

        return currentY - y;
    }

    /// <summary>
    /// Measures the height of word-wrapped text without drawing it.
    /// </summary>
    public static int MeasureWrappedHeight(SpriteFont font, string text, int maxWidth)
    {
        List<string> lines = WrapText(font, text, maxWidth);
        return lines.Count * font.LineSpacing;
    }

    private static List<string> WrapText(SpriteFont font, string text, int maxWidth)
    {
        var lines = new List<string>();
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string currentLine = "";
        foreach (string word in words)
        {
            string testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
            if (font.MeasureString(testLine).X > maxWidth && currentLine.Length > 0)
            {
                lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine);

        return lines;
    }
}
