using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Draws a <see cref="Nameplate"/> (a rounded panel with a centred title and stacked <see cref="NameplateBar"/>s)
    /// anchored above a world point - the MMO-style successor to <see cref="WorldLabel"/>. It projects
    /// <c>worldPos + offset</c> through the camera's <see cref="IIsoCamera3D.WorldToScreen"/>, centres the panel
    /// horizontally on that pixel and bottom-anchors it there (so the plate floats above the head), then draws the
    /// panel, title and bars screen-space via <see cref="SpriteBatch.DrawRounded"/> and
    /// <see cref="SpriteBatch.DrawString(SpriteFont,string,Vector2,Color,float)"/> on the shared white texture.
    /// </summary>
    /// <remarks>
    /// Call it from the consumer's 2D pass, BETWEEN the <see cref="SpriteBatch"/>'s <c>Begin</c>/<c>End</c> and after
    /// the 3D scene is drawn. Like <see cref="WorldLabel"/> it is screen-space and NOT depth-tested, so a plate is not
    /// hidden behind terrain or props (occlusion is out of scope). Plates that are empty, behind the camera, off the
    /// depth range, or beyond <c>maxDistance</c> are skipped (returns <c>false</c>). No per-frame heap allocation.
    /// </remarks>
    public static class NameplateRenderer
    {
        /// <summary>
        /// The distance cull predicate, shared with <see cref="WorldLabel.ShouldCull"/> so nameplates and labels cull
        /// identically. Returns <c>true</c> when <paramref name="maxDistance"/> &gt; 0 and <paramref name="worldPos"/>
        /// is farther than that from <paramref name="cullFrom"/>. A <paramref name="maxDistance"/> of 0 never culls.
        /// </summary>
        public static bool ShouldCull(Vector3 worldPos, Vector3 cullFrom, float maxDistance)
            => WorldLabel.ShouldCull(worldPos, cullFrom, maxDistance);

        /// <summary>
        /// Projects and draws one nameplate. Returns <c>true</c> if drawn, <c>false</c> if culled (empty plate, behind
        /// the camera, off-screen/off the depth range, or beyond <paramref name="maxDistance"/>).
        /// </summary>
        /// <param name="batch">An in-progress (Begun) sprite batch to draw into.</param>
        /// <param name="font">The font the title renders with.</param>
        /// <param name="white">A 1x1 white texture for the solid panel/bar fills (the diagnostics-overlay convention).</param>
        /// <param name="camera">The camera whose projection places the plate.</param>
        /// <param name="worldPos">The world anchor (e.g. the avatar's feet/centre).</param>
        /// <param name="offset">World offset added before projecting (e.g. <c>(0, headHeight, 0)</c> to float above the head).</param>
        /// <param name="plate">The nameplate model (title + bars).</param>
        /// <param name="style">The look (panel, padding, bar geometry).</param>
        /// <param name="viewportWidth">Framebuffer width in pixels.</param>
        /// <param name="viewportHeight">Framebuffer height in pixels.</param>
        /// <param name="maxDistance">If &gt; 0, plates whose anchor is farther than this from <paramref name="cullFrom"/>
        /// (or the camera eye when null) are culled. 0 draws regardless of distance.</param>
        /// <param name="cullFrom">Optional anchor the <paramref name="maxDistance"/> ring is measured from; null = the
        /// camera eye. Pass the viewer-player's position so plates cull on player-to-target distance (matches
        /// <see cref="WorldLabel"/>).</param>
        public static bool Draw(
            SpriteBatch batch, SpriteFont font, Texture2D white,
            IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
            in Nameplate plate, in NameplateStyle style,
            int viewportWidth, int viewportHeight,
            float maxDistance = 0f, Vector3? cullFrom = null)
        {
            if (batch is null || font is null || white is null || camera is null || plate.IsEmpty) return false;

            Vector3 cullOrigin = cullFrom ?? camera.Eye;
            if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
            if (!camera.WorldToScreen(worldPos + offset, viewportWidth, viewportHeight, out Vector2 pixel)) return false;

            Vector2 size = NameplateLayout.Measure(font, plate, style);
            if (size.X <= 0f || size.Y <= 0f) return false;

            // Centre horizontally on the projected pixel; bottom-anchor the panel there so it floats above the head.
            float left = pixel.X - size.X * 0.5f;
            float top = pixel.Y - size.Y;
            var panelRect = new Vector4(left, top, size.X, size.Y);

            // Panel fill (skip when transparent -> panel-less look) then optional border.
            if (style.PanelFill.A > 0f)
                batch.DrawRounded(white, panelRect, style.PanelFill, style.CornerRadius);
            if (style.PanelBorderThickness > 0f && style.PanelBorder.A > 0f)
                batch.DrawRounded(white, panelRect, style.PanelBorder, style.CornerRadius, 0f, style.PanelBorderThickness);

            float innerLeft = left + style.PadX;
            float innerTop = top + style.PadY;
            float innerW = size.X - 2f * style.PadX;

            // Title, centred in the top padded row. Shadow pass first (if configured), then the title colour.
            float titleH = 0f;
            if (!string.IsNullOrEmpty(plate.Title))
            {
                string title = style.MaxWidth > 0f
                    ? NameplateLayout.Ellipsize(font, plate.Title, innerW, style.FontScale)
                    : plate.Title;
                Vector2 tm = font.Measure(title) * style.FontScale;
                titleH = tm.Y;
                float titleX = innerLeft + (innerW - tm.X) * 0.5f;
                if (style.TitleShadow is Color shadow)
                    batch.DrawString(font, title,
                        new Vector2(titleX + style.TitleShadowOffset.X, innerTop + style.TitleShadowOffset.Y),
                        shadow, style.FontScale);
                batch.DrawString(font, title, new Vector2(titleX, innerTop), plate.TitleColor, style.FontScale);
            }

            // Bars stacked full inner width below the title; compute each rect in the loop (no per-frame list alloc).
            int barCount = plate.Bars?.Count ?? 0;
            float y = innerTop + titleH;
            bool contentAbove = titleH > 0f;
            for (int i = 0; i < barCount; i++)
            {
                if (contentAbove) y += style.BarSpacing;
                NameplateBar bar = plate.Bars[i];
                batch.DrawRounded(white, new Vector4(innerLeft, y, innerW, style.BarHeight), bar.Track, style.BarCornerRadius);
                float f = bar.ClampedFraction;
                if (f > 0f)
                    batch.DrawRounded(white, new Vector4(innerLeft, y, innerW * f, style.BarHeight), bar.Fill, style.BarCornerRadius);
                y += style.BarHeight;
                contentAbove = true;
            }
            return true;
        }
    }
}
