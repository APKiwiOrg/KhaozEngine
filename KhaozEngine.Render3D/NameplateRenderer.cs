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
    /// panel, title and bars screen-space via <see cref="SpriteBatch.DrawRounded(Texture2D, Vector4, Color, float, float, float, float)"/> and
    /// <see cref="SpriteBatch.DrawString(SpriteFont,string,Vector2,Color,float)"/> on the shared white texture.
    /// The placement itself (baseline, plus the optional <see cref="NameplateStyle.EdgeBehavior"/> clamp/deflect)
    /// is <see cref="NameplatePlacement.Place"/>. This class projects the anchor and draws the result.
    /// </summary>
    /// <remarks>
    /// Call it from the consumer's 2D pass, BETWEEN the <see cref="SpriteBatch"/>'s <c>Begin</c>/<c>End</c> and after
    /// the 3D scene is drawn. Like <see cref="WorldLabel"/> it is screen-space and NOT depth-tested, so a plate is not
    /// hidden behind terrain or props (occlusion is out of scope). Plates that are empty, behind the camera, off the
    /// depth range, or beyond <c>maxDistance</c> are skipped (returns <c>false</c>). No per-frame heap allocation.
    /// <see cref="NameplateEdgeBehavior.Deflect"/>'s hysteresis only works through the stateful overload below: the
    /// stateless <see cref="Draw(SpriteBatch,SpriteFont,Texture2D,IIsoCamera3D,Vector3,Vector3,in Nameplate,in NameplateStyle,int,int,float,Vector3?)"/>
    /// re-evaluates from a fresh <see cref="NameplatePlacementState"/> every call, which is fine for <see
    /// cref="NameplateEdgeBehavior.None"/> and <see cref="NameplateEdgeBehavior.Clamp"/> (both stateless) but means
    /// a Deflect plate drawn through it can never stay deflected across frames. A game tracking per-entity
    /// nameplates should keep one <see cref="NameplatePlacementState"/> per plate and call the stateful overload,
    /// and must not share that state across plates: doing so lets one plate's deflection leak into another's.
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
        /// <remarks>
        /// Stateless convenience overload: it evaluates <see cref="NameplatePlacement.Place"/> from a fresh <see
        /// cref="NameplatePlacementState"/> every call, so <see cref="NameplateStyle.EdgeBehavior"/> of <see
        /// cref="NameplateEdgeBehavior.None"/> or <see cref="NameplateEdgeBehavior.Clamp"/> behave identically to
        /// the stateful overload. <see cref="NameplateEdgeBehavior.Deflect"/>'s hysteresis needs state carried
        /// across frames, so use the overload below (with a <see cref="NameplatePlacementState"/> kept per plate)
        /// for that behaviour instead.
        /// </remarks>
        /// <param name="batch">An in-progress (Begun) sprite batch to draw into.</param>
        /// <param name="font">The font the title renders with.</param>
        /// <param name="white">A 1x1 white texture for the solid panel/bar fills (the diagnostics-overlay convention).</param>
        /// <param name="camera">The camera whose projection places the plate.</param>
        /// <param name="worldPos">The world anchor (e.g. the avatar's feet/centre).</param>
        /// <param name="offset">World offset added before projecting (e.g. <c>(0, headHeight, 0)</c> to float above the head).</param>
        /// <param name="plate">The nameplate model (title + bars).</param>
        /// <param name="style">The look (panel, padding, bar geometry, and <see cref="NameplateStyle.EdgeBehavior"/>).</param>
        /// <param name="viewportWidth">Framebuffer width in pixels.</param>
        /// <param name="viewportHeight">Framebuffer height in pixels.</param>
        /// <param name="maxDistance">If &gt; 0, plates whose anchor is farther than this from <paramref name="cullFrom"/>
        /// (or the camera eye when null) are culled. 0 draws regardless of distance.</param>
        /// <param name="cullFrom">Optional anchor the <paramref name="maxDistance"/> ring is measured from, or the
        /// camera eye when null. Pass the viewer-player's position so plates cull on player-to-target distance
        /// (matches <see cref="WorldLabel"/>).</param>
        public static bool Draw(
            SpriteBatch batch, SpriteFont font, Texture2D white,
            IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
            in Nameplate plate, in NameplateStyle style,
            int viewportWidth, int viewportHeight,
            float maxDistance = 0f, Vector3? cullFrom = null)
        {
            NameplatePlacementState state = default;
            return Draw(batch, font, white, camera, worldPos, offset, plate, style,
                viewportWidth, viewportHeight, ref state, maxDistance, cullFrom);
        }

        /// <summary>
        /// Projects and draws one nameplate, the same as the overload above, but with a caller-held <paramref
        /// name="placementState"/> so <see cref="NameplateEdgeBehavior.Deflect"/>'s hysteresis works across
        /// frames. Keep one <see cref="NameplatePlacementState"/> per plate/entity and pass the same instance in
        /// every frame. Do not share one instance across plates, or one plate's deflection leaks into another's.
        /// </summary>
        /// <param name="batch">An in-progress (Begun) sprite batch to draw into.</param>
        /// <param name="font">The font the title renders with.</param>
        /// <param name="white">A 1x1 white texture for the solid panel/bar fills (the diagnostics-overlay convention).</param>
        /// <param name="camera">The camera whose projection places the plate.</param>
        /// <param name="worldPos">The world anchor (e.g. the avatar's feet/centre).</param>
        /// <param name="offset">World offset added before projecting (e.g. <c>(0, headHeight, 0)</c> to float above the head).</param>
        /// <param name="plate">The nameplate model (title + bars).</param>
        /// <param name="style">The look (panel, padding, bar geometry, and <see cref="NameplateStyle.EdgeBehavior"/>).</param>
        /// <param name="viewportWidth">Framebuffer width in pixels.</param>
        /// <param name="viewportHeight">Framebuffer height in pixels.</param>
        /// <param name="placementState">This plate's carried-over <see cref="NameplateEdgeBehavior.Deflect"/> state.
        /// Keep one instance per plate across frames. A fresh instance behaves as not-yet-deflected.</param>
        /// <param name="maxDistance">If &gt; 0, plates whose anchor is farther than this from <paramref name="cullFrom"/>
        /// (or the camera eye when null) are culled. 0 draws regardless of distance.</param>
        /// <param name="cullFrom">Optional anchor the <paramref name="maxDistance"/> ring is measured from, or the
        /// camera eye when null. Pass the viewer-player's position so plates cull on player-to-target distance
        /// (matches <see cref="WorldLabel"/>).</param>
        public static bool Draw(
            SpriteBatch batch, SpriteFont font, Texture2D white,
            IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
            in Nameplate plate, in NameplateStyle style,
            int viewportWidth, int viewportHeight,
            ref NameplatePlacementState placementState,
            float maxDistance = 0f, Vector3? cullFrom = null)
        {
            if (batch is null || font is null || white is null || camera is null || plate.IsEmpty) return false;

            Vector3 cullOrigin = cullFrom ?? camera.Eye;
            if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
            if (!camera.WorldToScreen(worldPos + offset, viewportWidth, viewportHeight, out Vector2 pixel)) return false;

            Vector2 size = NameplateLayout.Measure(font, plate, style);
            if (size.X <= 0f || size.Y <= 0f) return false;

            // NameplatePlacement centres the panel horizontally on the projected pixel and bottom-anchors it
            // there by default (so it floats above the head), then applies style.EdgeBehavior on top.
            Vector4 panelRect = NameplatePlacement.Place(pixel, size, viewportWidth, viewportHeight, style, ref placementState);
            float left = panelRect.X;
            float top = panelRect.Y;

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
            float y = innerTop + titleH;
            bool contentAbove = titleH > 0f;
            if (plate.Bars is { } bars)
            {
                for (int i = 0; i < bars.Count; i++)
                {
                    if (contentAbove) y += style.BarSpacing;
                    NameplateBar bar = bars[i];
                    batch.DrawRounded(white, new Vector4(innerLeft, y, innerW, style.BarHeight), bar.Track, style.BarCornerRadius);
                    float f = bar.ClampedFraction;
                    if (f > 0f)
                        batch.DrawRounded(white, new Vector4(innerLeft, y, innerW * f, style.BarHeight), bar.Fill, style.BarCornerRadius);
                    y += style.BarHeight;
                    contentAbove = true;
                }
            }
            return true;
        }
    }
}
