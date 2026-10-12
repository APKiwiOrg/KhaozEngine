using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>Eight-way text outlines with headless offset and measurement helpers.</summary>
    public static class TextOutline
    {
        /// <summary>Writes the eight neighbouring offsets, ordered by row from top-left to bottom-right.</summary>
        /// <exception cref="ArgumentException">The destination has fewer than eight elements.</exception>
        public static void Offsets(float thickness, Span<Vector2> destination)
        {
            if (destination.Length < 8)
                throw new ArgumentException("The destination must have room for eight offsets.", nameof(destination));

            destination[0] = new Vector2(-thickness, -thickness);
            destination[1] = new Vector2(0f, -thickness);
            destination[2] = new Vector2(thickness, -thickness);
            destination[3] = new Vector2(-thickness, 0f);
            destination[4] = new Vector2(thickness, 0f);
            destination[5] = new Vector2(-thickness, thickness);
            destination[6] = new Vector2(0f, thickness);
            destination[7] = new Vector2(thickness, thickness);
        }

        /// <summary>Measures scaled text with the outline thickness added on both sides of each axis.</summary>
        public static Vector2 Measure(ITextMeasurer font, string text, float scale, float thickness) =>
            font.Measure(text) * scale + new Vector2(2f * thickness);

        /// <summary>
        /// Draws the eight outline passes before the fill at <paramref name="position"/>.
        /// Non-positive thickness draws only the fill. Outline offsets use the position's coordinate space.
        /// </summary>
        public static void DrawStringOutlined(this SpriteBatch batch, SpriteFont font, string text,
            Vector2 position, Color color, Color outline, float thickness, float scale)
        {
            if (thickness > 0f)
            {
                Span<Vector2> offsets = stackalloc Vector2[8];
                Offsets(thickness, offsets);
                foreach (Vector2 offset in offsets)
                    batch.DrawString(font, text, position + offset, outline, scale);
            }

            batch.DrawString(font, text, position, color, scale);
        }
    }
}
