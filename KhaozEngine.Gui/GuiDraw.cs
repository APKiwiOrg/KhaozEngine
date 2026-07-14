using System;
using System.Numerics;
using KhaozEngine.App;
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

        /// <summary>Return <paramref name="color"/> with its alpha scaled by <paramref name="opacity"/> (RGB kept).
        /// The shared fade knob for the opt-in overlay chrome (sliding panels, fading dropdowns).</summary>
        public static Vector4 WithOpacity(Vector4 color, float opacity) =>
            new(color.X, color.Y, color.Z, color.W * opacity);

        /// <summary>
        /// The three vertices of a chevron caret centred on <paramref name="center"/>: a downward "v" (arms up,
        /// apex down) by default, an upward "^" when <paramref name="pointingUp"/>. Pure geometry so the
        /// open/closed direction is headless-testable; <see cref="Caret"/> strokes it.
        /// </summary>
        public static (Vector2 left, Vector2 mid, Vector2 right) CaretGeometry(
            Vector2 center, float halfWidth, float halfHeight, bool pointingUp)
        {
            float armY = pointingUp ? center.Y + halfHeight : center.Y - halfHeight;
            float apexY = pointingUp ? center.Y - halfHeight : center.Y + halfHeight;
            return (new Vector2(center.X - halfWidth, armY),
                    new Vector2(center.X, apexY),
                    new Vector2(center.X + halfWidth, armY));
        }

        /// <summary>Draw a <paramref name="thickness"/>-wide line from <paramref name="a"/> to <paramref name="b"/>
        /// as a single rotated quad (the 1x1 white texture; Render2D has no line primitive).</summary>
        public static void Line(SpriteBatch batch, Texture2D white, Vector2 a, Vector2 b, float thickness, Vector4 color)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len <= 0f) return;
            float rot = System.MathF.Atan2(d.Y, d.X);
            batch.Draw(white, a, new Vector2(len, thickness), new Vector2(0f, 0.5f), rot,
                new Vector4(0f, 0f, 1f, 1f), (Color)color);
        }

        /// <summary>Stroke a chevron caret (see <see cref="CaretGeometry"/>) as two lines meeting at the apex.</summary>
        public static void Caret(SpriteBatch batch, Texture2D white, Vector2 center, float halfWidth, float halfHeight,
            bool pointingUp, float thickness, Vector4 color)
        {
            var (left, mid, right) = CaretGeometry(center, halfWidth, halfHeight, pointingUp);
            Line(batch, white, left, mid, thickness, color);
            Line(batch, white, mid, right, thickness, color);
        }

        /// <summary>
        /// Whole-unit DRAW geometry for an N-tab segmented strip over <paramref name="bounds"/>: the outer frame rect
        /// and the <c>count + 1</c> vertical edge positions (left..right), each rounded to a whole authoring unit so
        /// the tab bodies abut on a shared integer seam and the frame + interior dividers render as crisp single
        /// 1-unit lines even in a design pass that does no device-pixel snapping. This ROUNDS FOR DRAWING only and is
        /// independent of <see cref="TabBar.TabRect"/> (which stays fractional so hit-testing keeps its exact
        /// no-gap / last-edge-on-Right contract). The interior edges (<c>edges[1..count-1]</c>) are the divider
        /// positions. Pure geometry: no GPU, headless-testable.
        /// </summary>
        public static (Rect frame, float[] edges) TabStripDrawGeometry(Rect bounds, int count)
        {
            float[] edges = new float[count + 1];
            for (int i = 0; i <= count; i++)
                edges[i] = System.MathF.Round(bounds.X + bounds.Width * i / count);   // same split as TabRect, rounded
            float top = System.MathF.Round(bounds.Y);
            float bottom = System.MathF.Round(bounds.Bottom);
            return (new Rect(edges[0], top, edges[count] - edges[0], bottom - top), edges);
        }

        /// <summary>Draw a <paramref name="thickness"/>-px outline just inside <paramref name="r"/>. In a point-space
        /// UI pass the rect and thickness snap to whole device pixels so the outline is uniform (no fractional-phase
        /// asymmetry); the snap is a no-op in any other pass, so screen/design/world output is unchanged.</summary>
        public static void Border(SpriteBatch batch, Texture2D white, Rect r, float thickness, Vector4 color)
        {
            if (thickness <= 0f) return;
            r = batch.SnapRect(r);
            float t = batch.SnapLength(thickness, minDevicePixels: 1f);
            Fill(batch, white, new Rect(r.X, r.Y, r.Width, t), color);                       // top
            Fill(batch, white, new Rect(r.X, r.Bottom - t, r.Width, t), color);              // bottom
            Fill(batch, white, new Rect(r.X, r.Y, t, r.Height), color);                      // left
            Fill(batch, white, new Rect(r.Right - t, r.Y, t, r.Height), color);              // right
        }

        /// <summary>
        /// Fill <paramref name="r"/> honouring <paramref name="style"/>: when <see cref="GuiStyle.IsFlat"/> this is
        /// the exact plain single-quad <see cref="Fill"/> + <see cref="Border"/> (byte-identical to pre-7.7.0);
        /// otherwise it draws the soft shadow (when <see cref="GuiStyle.ShadowColor"/> is non-transparent), the rounded
        /// (optionally gradient) body, and the rounded border ring.
        /// <paramref name="bodyColor"/> is the resolved state colour (hover/press/etc.); <paramref name="borderColor"/>
        /// is the outline. In a point-space UI pass the rect + border thickness snap to whole device pixels (body and
        /// border share one snapped rect, so their edges stay aligned) for crisp uniform chrome; a no-op snap
        /// elsewhere leaves screen/design output unchanged.
        /// </summary>
        public static void FillStyled(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style,
            Vector4 bodyColor, Vector4 borderColor)
        {
            r = batch.SnapRect(r);

            // Skinned: the sprite owns the silhouette, so the procedural corner/border path is skipped. The drop
            // shadow still draws underneath (ShadowSize keeps working); the state colour multiplies over the skin.
            if (style.Skin is { HasTexture: true } skin)
            {
                if (style.ShadowSize > 0f && style.ShadowColor.W > 0f)
                    SoftRoundedQuad(batch, white, r, style.CornerRadius, style.ShadowColor, style.ShadowSize, style.ShadowOffset);
                DrawSkin(batch, skin, r, bodyColor);
                return;
            }

            float borderThickness = style.BorderThickness > 0f ? batch.SnapLength(style.BorderThickness, minDevicePixels: 1f) : 0f;

            if (style.IsFlat)
            {
                Fill(batch, white, r, bodyColor);
                Border(batch, white, r, borderThickness, borderColor);
                return;
            }

            var dest = new Vector4(r.X, r.Y, r.Width, r.Height);

            // Soft drop shadow under everything, as a body-edge bloom offset by ShadowOffset (same helper as the
            // hover glow). The body is drawn on top, so only the soft outer falloff shows.
            if (style.ShadowSize > 0f && style.ShadowColor.W > 0f)
                SoftRoundedQuad(batch, white, r, style.CornerRadius, style.ShadowColor, style.ShadowSize, style.ShadowOffset);

            // Rounded body: vertical gradient (scale of the state colour) or flat.
            Vector4 top = bodyColor, bottom = bodyColor;
            if (style.FillMode == GuiFill.VerticalGradient)
            {
                top = GuiStyle.ScaleRgb(bodyColor, style.GradientTopScale);
                bottom = GuiStyle.ScaleRgb(bodyColor, style.GradientBottomScale);
            }
            batch.DrawRounded(white, dest, new Vector4(0, 0, 1, 1), (Color)top, (Color)bottom, style.CornerRadius);

            // Rounded border ring.
            if (borderThickness > 0f)
                batch.DrawRounded(white, dest, (Color)borderColor, style.CornerRadius, softness: 0f, strokeWidth: borderThickness);
        }

        /// <summary>One nine-slice patch: a destination rect and the source UV sub-rect (u0,v0,u1,v1) to sample.</summary>
        public readonly record struct NineSlicePatch(Rect Dest, Vector4 Source);

        /// <summary>
        /// Number of native-size tiles needed to cover <paramref name="destExtent"/> with a tile of
        /// <paramref name="tilePx"/> draw units (the last tile is partial). <c>ceil(destExtent / tilePx)</c>, at least
        /// 1; a non-positive tile size (a degenerate skin with no centre) collapses to a single span. Pure.
        /// </summary>
        public static int TileCount(float destExtent, float tilePx)
        {
            if (tilePx <= 0f || destExtent <= 0f) return 1;
            return (int)MathF.Ceiling(destExtent / tilePx - 1e-4f);
        }

        /// <summary>
        /// Decompose a nine-slice <paramref name="skin"/> over destination <paramref name="dest"/> into the draw
        /// patches (dest rect + source UV). The four corners keep their source-pixel size (never scaled); the edges +
        /// centre stretch (<see cref="GuiSkinCenter.Stretch"/>) or repeat at native source-pixel size
        /// (<see cref="GuiSkinCenter.Tile"/>, clipping the last row/column). When the destination is too small for both
        /// opposing corners the destination insets scale down proportionally so the corners meet (the source stays
        /// fixed). Zero-area cells are dropped. Pure geometry (no GPU / no texture sampling), headless-testable.
        /// </summary>
        public static System.Collections.Generic.List<NineSlicePatch> NineSlicePatches(Rect dest, GuiSkin skin)
        {
            float l = MathF.Max(0f, skin.InsetLeft), t = MathF.Max(0f, skin.InsetTop);
            float rIn = MathF.Max(0f, skin.InsetRight), b = MathF.Max(0f, skin.InsetBottom);

            // Destination border insets = source-pixel insets (corners unscaled), scaled down proportionally when
            // the two opposing borders would overlap so the corners just meet. GuiSkin.DestinationInsets is the
            // single source of truth, shared with GuiStyle.ContentInsets so interior-content math always matches
            // what the nine-slice actually paints.
            Vector4 di = skin.DestinationInsets(dest);
            float dl = di.X, dt = di.Y, dr = di.Z, db = di.W;

            float[] dx = { dest.X, dest.X + dl, dest.Right - dr, dest.Right };
            float[] dy = { dest.Y, dest.Y + dt, dest.Bottom - db, dest.Bottom };

            // Source UV column/row edges. Insets in UV = source-span * (pixel-inset / source-pixel-size). The source
            // insets are NOT clamped (they stay fixed while the destination squishes).
            float u0 = skin.Source.X, u1 = skin.Source.Z, v0 = skin.Source.Y, v1 = skin.Source.W;
            float spW = skin.SourcePixelWidth, spH = skin.SourcePixelHeight;
            float su1 = u0 + (u1 - u0) * (spW > 0f ? l / spW : 0f);
            float su2 = u1 - (u1 - u0) * (spW > 0f ? rIn / spW : 0f);
            float sv1 = v0 + (v1 - v0) * (spH > 0f ? t / spH : 0f);
            float sv2 = v1 - (v1 - v0) * (spH > 0f ? b / spH : 0f);
            float[] su = { u0, su1, su2, u1 };
            float[] sv = { v0, sv1, sv2, v1 };

            // Native tile sizes (source centre-cell pixel extents), used only in Tile mode.
            bool tile = skin.Center == GuiSkinCenter.Tile;
            float tilePxW = MathF.Max(0f, spW - l - rIn);
            float tilePxH = MathF.Max(0f, spH - t - b);

            var patches = new System.Collections.Generic.List<NineSlicePatch>(tile ? 16 : 9);
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    var cell = new Rect(dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]);
                    if (cell.Width <= 0f || cell.Height <= 0f) continue;
                    var uv = new Vector4(su[col], sv[row], su[col + 1], sv[row + 1]);

                    // Corners (col/row 0 or 2) never tile; edges tile along their long axis; centre tiles both.
                    bool tileX = tile && col == 1;
                    bool tileY = tile && row == 1;
                    AppendCell(patches, cell, uv, tilePxW, tilePxH, tileX, tileY);
                }
            }
            return patches;
        }

        // Emit one nine-slice cell, either as a single stretched patch or as a grid of native-size tiles (clipping
        // the trailing partial tile's dest + source UV). tileX/tileY select which axes repeat.
        static void AppendCell(System.Collections.Generic.List<NineSlicePatch> outp, Rect cell, Vector4 uv,
            float tilePxW, float tilePxH, bool tileX, bool tileY)
        {
            int nx = tileX ? TileCount(cell.Width, tilePxW) : 1;
            int ny = tileY ? TileCount(cell.Height, tilePxH) : 1;
            float u0 = uv.X, v0 = uv.Y, u1 = uv.Z, v1 = uv.W;

            for (int iy = 0; iy < ny; iy++)
            {
                float y = cell.Y + (tileY ? iy * tilePxH : 0f);
                float h = tileY ? MathF.Min(tilePxH, cell.Bottom - y) : cell.Height;
                if (h <= 0f) continue;
                float fv = tileY ? h / tilePxH : 1f;   // fraction of the tile shown (1 unless clipped)

                for (int ix = 0; ix < nx; ix++)
                {
                    float x = cell.X + (tileX ? ix * tilePxW : 0f);
                    float w = tileX ? MathF.Min(tilePxW, cell.Right - x) : cell.Width;
                    if (w <= 0f) continue;
                    float fu = tileX ? w / tilePxW : 1f;

                    var src = new Vector4(u0, v0, u0 + (u1 - u0) * fu, v0 + (v1 - v0) * fv);
                    outp.Add(new NineSlicePatch(new Rect(x, y, w, h), src));
                }
            }
        }

        // Draw a nine-slice skin over r, tinting every patch by tint (the resolved state colour multiplied over the
        // sprite). Shared by FillStyled's skin path.
        static void DrawSkin(SpriteBatch batch, GuiSkin skin, Rect r, Vector4 tint)
        {
            var col = (Color)tint;
            foreach (NineSlicePatch p in NineSlicePatches(r, skin))
                batch.Draw(skin.Texture, new Vector4(p.Dest.X, p.Dest.Y, p.Dest.Width, p.Dest.Height), p.Source, col);
        }

        /// <summary>
        /// Draw a hover glow halo behind <paramref name="r"/> (additive) when the style enables it. A soft bloom
        /// that peaks at the body edge and fades smoothly to zero <c>GlowSize</c> draw units out; the caller draws
        /// the body on top, hiding the steep inner half so only the outer halo reads. Call this BEFORE the body.
        /// </summary>
        public static void HoverGlow(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style)
        {
            if (style.GlowSize <= 0f || style.GlowColor.W <= 0f) return;
            var prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;
            SoftRoundedQuad(batch, white, r, style.CornerRadius, style.GlowColor, style.GlowSize, Vector2.Zero);
            batch.BlendMode = prev;
        }

        /// <summary>
        /// Draw a soft rounded "bloom" of <paramref name="color"/> around <paramref name="body"/> (optionally
        /// shifted by <paramref name="offset"/>): the SDF falloff peaks (coverage 0.5) on the body outline and
        /// fades to zero <paramref name="spread"/> draw units outward, fully resolved inside the quad geometry so
        /// there is no hard rim. Shared by <see cref="HoverGlow"/> (additive) and the <see cref="FillStyled"/> drop
        /// shadow (alpha); the caller sets the blend mode. The body is expected to be drawn over the top.
        /// </summary>
        static void SoftRoundedQuad(SpriteBatch batch, Texture2D white, Rect body, float cornerRadius,
            Vector4 color, float spread, Vector2 offset)
        {
            var (quad, softness, inset) = SoftQuadGeometry(body, spread, offset);
            batch.DrawRounded(white, quad, (Color)color, cornerRadius, softness: softness, inset: inset);
        }

        /// <summary>
        /// Pure geometry for <see cref="SoftRoundedQuad"/>: given a <paramref name="body"/> rect, a
        /// <paramref name="spread"/> (how far the bloom reaches beyond the body) and an <paramref name="offset"/>,
        /// returns the expanded quad, the SDF <c>softness</c> and the <c>inset</c> to pass to
        /// <see cref="SpriteBatch.DrawRounded(Texture2D, Vector4, Color, float, float, float, float)"/>.
        /// <para>
        /// The SDF box is kept body-sized (so its <c>d=0</c> edge lies on the body outline) by insetting the quad
        /// back down: <c>softness = 2*spread</c> (coverage 0.5 at the body edge falls to 0 over <c>spread</c>
        /// units), and the quad is grown by <c>2*spread</c> per side (== <c>inset</c>) so even the rounded corners,
        /// where the SDF distance grows ~√2 faster, resolve to zero well before the quad edge. Pure / headless.
        /// </para>
        /// </summary>
        internal static (Vector4 quad, float softness, float inset) SoftQuadGeometry(Rect body, float spread, Vector2 offset)
        {
            float pad = spread * 2f;
            float softness = spread * 2f;
            var quad = new Vector4(
                body.X + offset.X - pad,
                body.Y + offset.Y - pad,
                body.Width + pad * 2f,
                body.Height + pad * 2f);
            return (quad, softness, pad);
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
        public static void DrawButton(SpriteBatch batch, Texture2D white, SpriteFont font, Rect rect, LocalizedText label,
            in GuiStyle style, bool enabled, bool selected, bool hover, bool press, float scale = 1f)
        {
            Vector4 fill = !enabled ? style.DisabledFill
                : selected ? style.SelectedFill
                : press ? style.Press
                : hover ? style.Hover
                : style.Fill;
            Vector4 border = selected ? style.SelectedBorder : style.Border;
            Vector4 text = enabled ? style.Text : style.DisabledText;

            // Snap the body rect once (a no-op outside a point-space pass) so the fill, border, and the centred
            // label all lay out against the same device-aligned rect; FillStyled re-snaps idempotently.
            rect = batch.SnapRect(rect);

            if (hover && enabled) HoverGlow(batch, white, rect, style);
            FillStyled(batch, white, rect, style, fill, border);

            string s = label.Resolve();
            var pos = AlignedTextPos(rect, font.Measure(s), font.LineHeight, GuiAlign.Center, scale, pad: 0f);
            batch.DrawString(font, s, pos, (Color)text, scale);
        }

        /// <summary>
        /// The shared top-left draw position for a single line of pre-measured text placed inside
        /// <paramref name="rect"/>: horizontally aligned per <paramref name="align"/> within
        /// [<c>rect.X + pad</c>, <c>rect.Right - pad</c>] and vertically centred. <paramref name="measured"/> is the
        /// UNSCALED <c>font.Measure(text)</c>; the width and the vertical centring both multiply by
        /// <paramref name="scale"/> so a caller that draws the text at <paramref name="scale"/> stays aligned
        /// (<c>scale = 1</c> reproduces the unscaled layout exactly). Pure math: no GPU, headless-testable.
        /// </summary>
        public static Vector2 AlignedTextPos(Rect rect, Vector2 measured, float lineHeight, GuiAlign align, float scale = 1f, float pad = 0f)
        {
            float w = measured.X * scale;
            float x = align switch
            {
                GuiAlign.Left => rect.X + pad,
                GuiAlign.Right => rect.Right - w - pad,
                _ => rect.X + (rect.Width - w) * 0.5f,
            };
            float y = rect.Y + (rect.Height - lineHeight * scale) * 0.5f;
            return new Vector2(x, y);
        }

        /// <summary>
        /// Fit a single line of text into <paramref name="maxWidth"/>: returns <paramref name="text"/> unchanged
        /// when it already fits, otherwise the longest prefix that fits with a trailing "..." appended (three
        /// ASCII dots, never the single-glyph ellipsis, which may not be baked into a font atlas).
        /// <paramref name="measureWidth"/> is the caller's width function (e.g. <c>s =&gt; font.Measure(s).X</c>),
        /// so the helper is pure and headless-testable. When not even the dots fit, "..." is still returned (the
        /// caller's scissor clips the residue - dots beat drawing nothing). Width is assumed monotonic in prefix
        /// length, so the fitting prefix is found by binary search.
        /// </summary>
        public static string TruncateWithEllipsis(string text, float maxWidth, Func<string, float> measureWidth)
        {
            if (string.IsNullOrEmpty(text) || measureWidth(text) <= maxWidth) return text;

            const string Ellipsis = "...";
            int lo = 0, hi = text.Length - 1;   // hi < Length: the full text already failed the fit test
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (measureWidth(text[..mid] + Ellipsis) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return text[..lo] + Ellipsis;
        }
    }
}
