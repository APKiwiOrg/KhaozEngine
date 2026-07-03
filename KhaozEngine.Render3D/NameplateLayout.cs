using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure, GPU-free layout math for a <see cref="Nameplate"/> - the panel outer size from the title width, bar
    /// count, padding and spacing. No camera, no device, so it is headless-testable; <see cref="NameplateRenderer"/>
    /// projects the world point and applies the placement formula on top of this.
    /// </summary>
    public static class NameplateLayout
    {
        /// <summary>
        /// The panel outer size (width, height) in screen pixels for <paramref name="plate"/> under
        /// <paramref name="style"/>, measuring the title with <paramref name="font"/>. The width is the title width
        /// floored to <see cref="NameplateStyle.MinBarWidth"/>, plus horizontal padding, capped at
        /// <see cref="NameplateStyle.MaxWidth"/> when set. The height stacks the title row and each
        /// <see cref="NameplateStyle.BarHeight"/> bar, with a <see cref="NameplateStyle.BarSpacing"/> gap before every
        /// row that has content above it, plus vertical padding. An empty plate (see <see cref="Nameplate.IsEmpty"/>)
        /// measures to <see cref="Vector2.Zero"/>.
        /// </summary>
        public static Vector2 Measure(ITextMeasurer font, in Nameplate plate, in NameplateStyle style)
        {
            if (plate.IsEmpty) return Vector2.Zero;

            float titleW = 0f, titleH = 0f;
            if (!string.IsNullOrEmpty(plate.Title))
            {
                Vector2 m = font.Measure(plate.Title) * style.FontScale;
                titleW = m.X; titleH = m.Y;
            }

            int barCount = plate.Bars?.Count ?? 0;
            float innerW = System.MathF.Max(titleW, style.MinBarWidth);

            // A spacing gap precedes each row (title or previous bar) that already has content above it.
            float contentH = titleH;
            for (int i = 0; i < barCount; i++)
            {
                if (contentH > 0f) contentH += style.BarSpacing;
                contentH += style.BarHeight;
            }

            float outerW = innerW + 2f * style.PadX;
            float outerH = contentH + 2f * style.PadY;
            if (style.MaxWidth > 0f) outerW = System.MathF.Min(outerW, style.MaxWidth);
            return new Vector2(outerW, outerH);
        }

        /// <summary>
        /// The largest prefix of <paramref name="text"/> that fits <paramref name="maxWidth"/> pixels at
        /// <paramref name="scale"/>, suffixed with three ASCII dots when it had to be trimmed (the baked font only
        /// covers ASCII 32-126, so the single "…" glyph is deliberately avoided). Returns the text unchanged when it
        /// already fits or <paramref name="maxWidth"/> is non-positive. Used by <see cref="NameplateRenderer"/> to
        /// honour <see cref="NameplateStyle.MaxWidth"/>.
        /// </summary>
        internal static string Ellipsize(ITextMeasurer font, string text, float maxWidth, float scale)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text;
            if (font.Measure(text).X * scale <= maxWidth) return text;

            const string ellipsis = "...";
            float ellipsisW = font.Measure(ellipsis).X * scale;
            for (int len = text.Length - 1; len >= 1; len--)
            {
                if (font.Measure(text.Substring(0, len)).X * scale + ellipsisW <= maxWidth)
                    return text.Substring(0, len) + ellipsis;
            }
            return ellipsis;
        }
    }
}
