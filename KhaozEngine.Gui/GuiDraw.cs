using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Small rectangle-drawing helpers shared by the widgets, drawn with a 1x1 white texture through the
    /// <see cref="SpriteBatch"/> (Render2D has no primitive renderer; this fills that gap for Gui).
    /// </summary>
    internal static class GuiDraw
    {
        /// <summary>Fill <paramref name="r"/> with a solid color.</summary>
        public static void Fill(SpriteBatch batch, Texture2D white, Rect r, Vector4 color) =>
            batch.Draw(white, new Vector4(r.X, r.Y, r.Width, r.Height), color);

        /// <summary>Draw a <paramref name="thickness"/>-px outline just inside <paramref name="r"/>.</summary>
        public static void Border(SpriteBatch batch, Texture2D white, Rect r, float thickness, Vector4 color)
        {
            if (thickness <= 0f) return;
            float t = thickness;
            Fill(batch, white, new Rect(r.X, r.Y, r.Width, t), color);                       // top
            Fill(batch, white, new Rect(r.X, r.Bottom - t, r.Width, t), color);              // bottom
            Fill(batch, white, new Rect(r.X, r.Y, t, r.Height), color);                      // left
            Fill(batch, white, new Rect(r.Right - t, r.Y, t, r.Height), color);              // right
        }

        /// <summary>
        /// The single source of truth for button visuals, shared by the immediate <see cref="GuiSurface.Button(SpriteFont, Rect, string, GuiStyle, bool, bool)"/>
        /// and the retained <see cref="Button"/>. Draws the fill (priority: <c>!enabled</c>→DisabledFill,
        /// <paramref name="selected"/>→SelectedFill, <paramref name="press"/>→Press, <paramref name="hover"/>→Hover,
        /// else Fill), the border (selected→SelectedBorder else Border, <c>style.BorderThickness</c>), and the
        /// centred <paramref name="label"/> (enabled→Text else DisabledText).
        /// </summary>
        public static void DrawButton(SpriteBatch batch, Texture2D white, SpriteFont font, Rect rect, string label,
            in GuiStyle style, bool enabled, bool selected, bool hover, bool press)
        {
            Vector4 fill = !enabled ? style.DisabledFill
                : selected ? style.SelectedFill
                : press ? style.Press
                : hover ? style.Hover
                : style.Fill;
            Vector4 border = selected ? style.SelectedBorder : style.Border;
            Vector4 text = enabled ? style.Text : style.DisabledText;

            Fill(batch, white, rect, fill);
            Border(batch, white, rect, style.BorderThickness, border);

            Vector2 size = font.Measure(label);
            var pos = new Vector2(
                rect.X + (rect.Width - size.X) * 0.5f,
                rect.Y + (rect.Height - font.LineHeight) * 0.5f);
            batch.DrawString(font, label, pos, text);
        }
    }
}
