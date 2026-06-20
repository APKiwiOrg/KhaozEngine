using System.Numerics;
using KhaozEngine.Primitives;
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
            batch.Draw(white, new Vector4(r.X, r.Y, r.Width, r.Height), (Color)color);

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
        /// Fill <paramref name="r"/> honouring <paramref name="style"/>: when <see cref="GuiStyle.IsFlat"/> this is
        /// the exact plain single-quad <see cref="Fill"/> + <see cref="Border"/> (byte-identical to pre-7.4.0);
        /// otherwise it draws the soft shadow, the rounded (optionally gradient) body, and the rounded border ring.
        /// <paramref name="bodyColor"/> is the resolved state colour (hover/press/etc.); <paramref name="borderColor"/>
        /// is the outline.
        /// </summary>
        public static void FillStyled(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style,
            Vector4 bodyColor, Vector4 borderColor)
        {
            if (style.IsFlat)
            {
                Fill(batch, white, r, bodyColor);
                Border(batch, white, r, style.BorderThickness, borderColor);
                return;
            }

            var dest = new Vector4(r.X, r.Y, r.Width, r.Height);

            // Soft drop shadow under everything.
            if (style.ShadowSize > 0f && style.ShadowColor.W > 0f)
            {
                var shadow = new Vector4(r.X + style.ShadowOffset.X, r.Y + style.ShadowOffset.Y, r.Width, r.Height);
                batch.DrawRounded(white, shadow, (Color)style.ShadowColor, style.CornerRadius, softness: style.ShadowSize);
            }

            // Rounded body: vertical gradient (scale of the state colour) or flat.
            Vector4 top = bodyColor, bottom = bodyColor;
            if (style.FillMode == GuiFill.VerticalGradient)
            {
                top = GuiStyle.ScaleRgb(bodyColor, style.GradientTopScale);
                bottom = GuiStyle.ScaleRgb(bodyColor, style.GradientBottomScale);
            }
            batch.DrawRounded(white, dest, new Vector4(0, 0, 1, 1), (Color)top, (Color)bottom, style.CornerRadius);

            // Rounded border ring.
            if (style.BorderThickness > 0f)
                batch.DrawRounded(white, dest, (Color)borderColor, style.CornerRadius, softness: 0f, strokeWidth: style.BorderThickness);
        }

        /// <summary>Draw a hover glow halo behind/around <paramref name="r"/> (additive) when the style enables it.</summary>
        public static void HoverGlow(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style)
        {
            if (style.GlowSize <= 0f || style.GlowColor.W <= 0f) return;
            var prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;
            float g = style.GlowSize;
            var dest = new Vector4(r.X - g * 0.5f, r.Y - g * 0.5f, r.Width + g, r.Height + g);
            batch.DrawRounded(white, dest, (Color)style.GlowColor, style.CornerRadius + g * 0.5f, softness: g);
            batch.BlendMode = prev;
        }

        /// <summary>
        /// The handle geometry for a horizontal slider track: a square knob the height of <paramref name="rect"/>
        /// (clamped to the rect width), and the travel range of its CENTRE. Insetting by the handle half-width is
        /// what lets the value reach exactly 0 and 1 without the knob spilling past the track ends. Shared by the
        /// input-mapping in <see cref="GuiSurface"/>.<c>Slider</c> and <see cref="DrawSlider"/> so both agree on
        /// where value <c>v</c> sits.
        /// </summary>
        public static (float half, float usable) SliderGeometry(Rect rect)
        {
            float handleW = System.MathF.Min(rect.Height, rect.Width);
            float half = handleW * 0.5f;
            float usable = System.MathF.Max(1f, rect.Width - handleW);
            return (half, usable);
        }

        /// <summary>
        /// Slider visuals: a thin track bar (<c>style.Fill</c>, or <c>DisabledFill</c>), an accent fill
        /// (<c>style.Border</c>) from the left end up to the handle when enabled, and a knob at value
        /// <paramref name="value01"/> (<c>style.Press</c> while <paramref name="dragging"/>, <c>style.Hover</c> while
        /// <paramref name="hover"/>, else <c>style.Fill</c>; <c>DisabledFill</c> when disabled). Geometry matches
        /// <see cref="SliderGeometry"/>.
        /// </summary>
        public static void DrawSlider(SpriteBatch batch, Texture2D white, Rect rect, float value01,
            in GuiStyle style, bool enabled, bool hover, bool dragging)
        {
            float v = value01 < 0f ? 0f : value01 > 1f ? 1f : value01;
            (float half, float usable) = SliderGeometry(rect);

            // Thin track bar centred vertically, spanning the handle-centre travel range.
            float trackH = System.MathF.Max(2f, rect.Height * 0.30f);
            float trackY = rect.Y + (rect.Height - trackH) * 0.5f;
            var track = new Rect(rect.X + half, trackY, usable, trackH);
            Fill(batch, white, track, enabled ? style.Fill : style.DisabledFill);

            float centerX = rect.X + half + v * usable;

            // Accent fill from the left end up to the handle (enabled only).
            if (enabled && centerX > track.X)
                Fill(batch, white, new Rect(track.X, trackY, centerX - track.X, trackH), style.Border);

            Vector4 knob = !enabled ? style.DisabledFill
                : dragging ? style.Press
                : hover ? style.Hover
                : style.Fill;
            var handle = new Rect(centerX - half, rect.Y, half * 2f, rect.Height);
            FillStyled(batch, white, handle, style, knob, enabled ? style.Border : style.DisabledText);
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

            if (hover && enabled) HoverGlow(batch, white, rect, style);
            FillStyled(batch, white, rect, style, fill, border);

            Vector2 size = font.Measure(label);
            var pos = new Vector2(
                rect.X + (rect.Width - size.X) * 0.5f,
                rect.Y + (rect.Height - font.LineHeight) * 0.5f);
            batch.DrawString(font, label, pos, (Color)text);
        }
    }
}
