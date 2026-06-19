using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Point-anchored text drawing over a <see cref="SpriteFont"/>: draw at a point, centered on a point,
    /// right-aligned to a point, centered in a <see cref="Rect"/>, or wrapped+centered. All positions are
    /// pixel-snapped (floored) to avoid sub-pixel blur. Colors are RGBA <see cref="Vector4"/> (0..1); the
    /// <c>alpha</c> overloads modulate the color's alpha by an extra factor (e.g. a fade).
    ///
    /// Complements <see cref="TextLayout"/> (which aligns/wraps within a width-region): this is the
    /// screen-author-friendly point API. The positioning math is split into pure, headless-testable helpers
    /// over <see cref="ITextMeasurer"/>; the <c>Draw*</c> methods add the GPU-backed <see cref="SpriteBatch"/>.
    /// The MonoGame-free 5.x port of the 4.x KhaozEngine.UI TextHelper.
    /// </summary>
    public static class TextHelper
    {
        // --- pure positioning (headless-testable over a fake ITextMeasurer) ---

        /// <summary>The X at which <paramref name="text"/> starts so it is horizontally centered on
        /// <paramref name="centerX"/>.</summary>
        public static float CenteredX(ITextMeasurer font, string text, float centerX) =>
            centerX - font.Measure(text).X * 0.5f;

        /// <summary>The X at which <paramref name="text"/> starts so its right edge lands on
        /// <paramref name="rightX"/>.</summary>
        public static float RightX(ITextMeasurer font, string text, float rightX) =>
            rightX - font.Measure(text).X;

        /// <summary>The top-left at which <paramref name="text"/> starts so it is centered both horizontally and
        /// vertically within <paramref name="rect"/>.</summary>
        public static Vector2 CenteredInRect(ITextMeasurer font, string text, Rect rect)
        {
            Vector2 size = font.Measure(text);
            return new Vector2(rect.X + (rect.Width - size.X) * 0.5f, rect.Y + (rect.Height - size.Y) * 0.5f);
        }

        /// <summary>Total height (pixels) of <paramref name="text"/> word-wrapped to <paramref name="maxWidth"/>.</summary>
        public static float MeasureWrappedHeight(ITextMeasurer font, string text, float maxWidth) =>
            TextLayout.MeasureWrappedHeight(font, text, maxWidth);

        // --- drawing (needs the GPU-backed SpriteFont) ---

        /// <summary>Draws <paramref name="text"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public static void Draw(SpriteBatch batch, SpriteFont font, string text, float x, float y, Color color) =>
            batch.DrawString(font, text, Snap(x, y), color);

        /// <summary>Draws <paramref name="text"/> at (<paramref name="x"/>, <paramref name="y"/>), alpha modulated by <paramref name="alpha"/>.</summary>
        public static void Draw(SpriteBatch batch, SpriteFont font, string text, float x, float y, Color color, float alpha) =>
            batch.DrawString(font, text, Snap(x, y), Fade(color, alpha));

        /// <summary>Draws <paramref name="text"/> horizontally centered on <paramref name="centerX"/> at <paramref name="y"/>.</summary>
        public static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, float centerX, float y, Color color) =>
            batch.DrawString(font, text, Snap(CenteredX(font, text, centerX), y), color);

        /// <summary>Draws <paramref name="text"/> centered on <paramref name="centerX"/>, alpha modulated by <paramref name="alpha"/>.</summary>
        public static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, float centerX, float y, Color color, float alpha) =>
            batch.DrawString(font, text, Snap(CenteredX(font, text, centerX), y), Fade(color, alpha));

        /// <summary>Draws <paramref name="text"/> right-aligned so its right edge lands on <paramref name="rightX"/> at <paramref name="y"/>.</summary>
        public static void DrawRight(SpriteBatch batch, SpriteFont font, string text, float rightX, float y, Color color) =>
            batch.DrawString(font, text, Snap(RightX(font, text, rightX), y), color);

        /// <summary>Draws <paramref name="text"/> right-aligned to <paramref name="rightX"/>, alpha modulated by <paramref name="alpha"/>.</summary>
        public static void DrawRight(SpriteBatch batch, SpriteFont font, string text, float rightX, float y, Color color, float alpha) =>
            batch.DrawString(font, text, Snap(RightX(font, text, rightX), y), Fade(color, alpha));

        /// <summary>Draws <paramref name="text"/> centered horizontally and vertically within <paramref name="rect"/>.</summary>
        public static void DrawCenteredInRect(SpriteBatch batch, SpriteFont font, string text, Rect rect, Color color)
        {
            Vector2 p = CenteredInRect(font, text, rect);
            batch.DrawString(font, text, Snap(p.X, p.Y), color);
        }

        /// <summary>Draws <paramref name="text"/> centered within <paramref name="rect"/>, alpha modulated by <paramref name="alpha"/>.</summary>
        public static void DrawCenteredInRect(SpriteBatch batch, SpriteFont font, string text, Rect rect, Color color, float alpha)
        {
            Vector2 p = CenteredInRect(font, text, rect);
            batch.DrawString(font, text, Snap(p.X, p.Y), Fade(color, alpha));
        }

        /// <summary>
        /// Word-wraps <paramref name="text"/> to <paramref name="maxWidth"/> and draws each line centered on
        /// <paramref name="centerX"/>, starting at <paramref name="y"/>. Returns the total height drawn.
        /// </summary>
        public static float DrawWrappedCentered(SpriteBatch batch, SpriteFont font, string text,
            float centerX, float y, float maxWidth, Color color, float alpha) =>
            TextLayout.DrawWrapped(batch, font, text,
                new Vector2(centerX - maxWidth * 0.5f, y), maxWidth, TextAlign.Center, Fade(color, alpha));

        // Pixel-snap to integer coordinates so glyphs land on texel boundaries (no sub-pixel blur).
        static Vector2 Snap(float x, float y) => new(MathF.Floor(x), MathF.Floor(y));

        // Multiply the color's alpha by an extra factor (RGB unchanged), clamped to [0, 1].
        static Color Fade(Color color, float alpha) =>
            color.WithAlpha(color.A * Math.Clamp(alpha, 0f, 1f));
    }
}
