using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>Horizontal alignment of text within a region.</summary>
    public enum TextAlign { Left, Center, Right }

    /// <summary>
    /// Pure text-layout helpers (word-wrap + alignment) over an <see cref="ITextMeasurer"/>, plus draw
    /// overloads that take a <see cref="SpriteBatch"/> + <see cref="SpriteFont"/>. The layout math is
    /// device-free and headless-testable. Ported from the 4.x <c>TextHelper</c> (which was MonoGame-bound).
    /// </summary>
    public static class TextLayout
    {
        // --- pure layout (headless-testable) ---

        /// <summary>The X (pixels) at which a line of <paramref name="text"/> starts so it aligns within
        /// [<paramref name="left"/>, <paramref name="left"/> + <paramref name="width"/>]. The measured width is
        /// multiplied by <paramref name="scale"/> so alignment stays correct for text drawn at that scale
        /// (<c>scale = 1</c> is the unscaled path).</summary>
        public static float AlignedX(ITextMeasurer font, string text, float left, float width, TextAlign align, float scale = 1f)
        {
            float textW = font.Measure(text).X * scale;
            return align switch
            {
                TextAlign.Center => left + (width - textW) * 0.5f,
                TextAlign.Right => left + width - textW,
                _ => left,
            };
        }

        /// <summary>Word-wraps <paramref name="text"/> so each line fits within <paramref name="maxWidth"/>
        /// pixels. A single word wider than the limit stays on its own line (never dropped).</summary>
        public static List<string> Wrap(ITextMeasurer font, string text, float maxWidth)
        {
            var lines = new List<string>();
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string current = "";
            foreach (string word in words)
            {
                string test = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.Measure(test).X > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = test;
                }
            }

            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        /// <summary>Total height (pixels) of <paramref name="text"/> word-wrapped to <paramref name="maxWidth"/>.</summary>
        public static float MeasureWrappedHeight(ITextMeasurer font, string text, float maxWidth) =>
            Wrap(font, text, maxWidth).Count * font.LineHeight;

        // --- drawing (needs the GPU-backed SpriteFont) ---

        /// <summary>Draws one line of <paramref name="text"/> horizontally aligned within
        /// [<paramref name="left"/>, <paramref name="left"/> + <paramref name="width"/>] at <paramref name="y"/>,
        /// uniformly scaled by <paramref name="scale"/> (<c>scale = 1</c> is the unscaled path).
        /// Positions are pixel-snapped to avoid sub-pixel blur.</summary>
        public static void DrawAligned(SpriteBatch batch, SpriteFont font, string text,
            float left, float width, float y, TextAlign align, Color color, float scale = 1f)
        {
            float x = MathF.Floor(AlignedX(font, text, left, width, align, scale));
            batch.DrawString(font, text, new Vector2(x, MathF.Floor(y)), color, scale);
        }

        /// <summary>Draws <paramref name="text"/> word-wrapped to <paramref name="maxWidth"/>, each line aligned
        /// within that width, starting at <paramref name="topLeft"/> and scaled by <paramref name="scale"/>
        /// (<c>scale = 1</c> is the unscaled path). Wrapping and line advance both account for the scale so the
        /// scaled lines fill <paramref name="maxWidth"/>. Returns the total height drawn.</summary>
        public static float DrawWrapped(SpriteBatch batch, SpriteFont font, string text,
            Vector2 topLeft, float maxWidth, TextAlign align, Color color, float scale = 1f)
        {
            float y = topLeft.Y;
            foreach (string line in Wrap(font, text, maxWidth / scale))
            {
                DrawAligned(batch, font, line, topLeft.X, maxWidth, y, align, color, scale);
                y += font.LineHeight * scale;
            }
            return y - topLeft.Y;
        }
    }
}
