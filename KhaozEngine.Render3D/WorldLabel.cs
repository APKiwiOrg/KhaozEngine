using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Draws a string anchored to a world point as a screen-space label - the centralized nameplate / world-text
    /// helper (e.g. a player name floating above their head). It projects the anchor pixel via
    /// <see cref="NameplateAnchorProjection.Project(IIsoCamera3D, Vector3, Vector3, int, int, out Vector2)"/> (the head point <c>worldPos + offset</c> drives screen Y, the
    /// body column <c>worldPos</c> plus only the lateral part of <c>offset</c> drives screen X, so a perspective
    /// camera's horizontal lean on an off-centre entity does not put the label beside it), then draws <c>text</c>
    /// centred horizontally and lifted so it sits above that pixel, via
    /// <see cref="SpriteBatch.DrawString(SpriteFont,string,Vector2,Color,float)"/>.
    /// Reuses the shipped <see cref="SpriteFont"/>/<see cref="SpriteBatch"/> text path - no per-name texture/atlas.
    /// </summary>
    /// <remarks>
    /// Call this from the consumer's 2D pass, BETWEEN the <see cref="SpriteBatch"/>'s <c>Begin</c>/<c>End</c> and
    /// after the 3D scene has been drawn. The label is screen-space and drawn on top: it is NOT depth-tested, so a
    /// name is not hidden when its owner stands behind terrain or a prop (occluded nameplates are out of scope).
    /// Labels that project behind the camera, out of the depth range, or beyond <c>maxDistance</c> are
    /// skipped (returns <c>false</c>).
    /// </remarks>
    public static class WorldLabel
    {
        /// <summary>
        /// Projects and draws one label. Returns <c>true</c> if it was drawn, <c>false</c> if culled (empty text,
        /// behind the camera, or beyond <paramref name="maxDistance"/>).
        /// </summary>
        /// <param name="batch">An in-progress (Begun) sprite batch to draw into.</param>
        /// <param name="font">The font to render with.</param>
        /// <param name="camera">The camera whose projection places the label.</param>
        /// <param name="worldPos">The world anchor (e.g. the avatar's feet/centre).</param>
        /// <param name="offset">World-space offset added to <paramref name="worldPos"/> before projecting (e.g.
        /// <c>(0, headHeight, 0)</c> to float the name above the head). Its full value (including the vertical part)
        /// drives the head point that decides screen Y. Only its lateral (X/Z) part feeds the body-column
        /// projection that decides screen X (see <see cref="NameplateAnchorProjection.Project(IIsoCamera3D, Vector3, Vector3, int, int, out Vector2)"/>), so a perspective
        /// camera's lean does not put the label beside the entity, while a deliberate lateral nudge still moves the
        /// anchor.</param>
        /// <param name="text">The label text (empty/null is a no-op).</param>
        /// <param name="color">Text color (RGBA).</param>
        /// <param name="viewportWidth">Framebuffer width in pixels. Framebuffer-space overload: pair it with a
        /// framebuffer-space drawing pass. A design-space HUD pass (a <c>SpriteBatch.Begin</c> with a design
        /// viewport) must use the <see cref="IDesignViewport"/> overload instead, or the label drifts on any
        /// window whose aspect differs from the design aspect.</param>
        /// <param name="viewportHeight">Framebuffer height in pixels.</param>
        /// <param name="scale">Uniform text scale (matches <see cref="SpriteFont.Measure(string)"/> * scale).</param>
        /// <param name="maxDistance">If &gt; 0, labels whose anchor is farther than this from <paramref name="cullFrom"/>
        /// (or the camera eye when <paramref name="cullFrom"/> is null) are culled. 0 (default) draws regardless of
        /// distance.</param>
        /// <param name="cullFrom">Optional anchor the <paramref name="maxDistance"/> ring is measured from. Defaults to
        /// null = the camera eye (the prior behaviour). Pass the viewer-player's position so nameplates cull on
        /// player-to-target distance rather than camera-to-target - with an orbit camera offset from the player the
        /// camera-eye ring pops labels in/out as the camera rotates even when nobody moves.</param>
        public static bool Draw(SpriteBatch batch, SpriteFont font, IIsoCamera3D camera, Vector3 worldPos,
            Vector3 offset, string text, Color color, int viewportWidth, int viewportHeight,
            float scale = 1f, float maxDistance = 0f, Vector3? cullFrom = null)
        {
            if (batch is null || font is null || camera is null || string.IsNullOrEmpty(text)) return false;
            Vector3 cullOrigin = cullFrom ?? camera.Eye;
            if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
            if (!NameplateAnchorProjection.Project(camera, worldPos, offset, viewportWidth, viewportHeight, out Vector2 pixel)) return false;

            DrawAt(batch, font, pixel, text, color, scale);
            return true;
        }

        /// <summary>
        /// Projects and draws one label for a DESIGN-SPACE HUD pass (a <c>SpriteBatch.Begin</c> taken with
        /// <paramref name="designViewport"/>), the design-space counterpart of the framebuffer-pixel overload
        /// above. Anchors via the design-aware <see cref="NameplateAnchorProjection.Project(IIsoCamera3D, Vector3,
        /// Vector3, IDesignViewport, out Vector2)"/> instead, so the label lands correctly on any window aspect.
        /// </summary>
        /// <param name="batch">An in-progress (Begun) sprite batch to draw into.</param>
        /// <param name="font">The font to render with.</param>
        /// <param name="camera">The camera whose projection places the label.</param>
        /// <param name="worldPos">The world anchor (e.g. the avatar's feet/centre).</param>
        /// <param name="offset">World-space offset added to <paramref name="worldPos"/> before projecting.</param>
        /// <param name="text">The label text (empty/null is a no-op).</param>
        /// <param name="color">Text color (RGBA).</param>
        /// <param name="designViewport">The design viewport the HUD pass is drawing through.</param>
        /// <param name="scale">Uniform text scale (matches <see cref="SpriteFont.Measure(string)"/> * scale).</param>
        /// <param name="maxDistance">If &gt; 0, labels whose anchor is farther than this from <paramref name="cullFrom"/>
        /// (or the camera eye when <paramref name="cullFrom"/> is null) are culled. 0 (default) draws regardless of
        /// distance.</param>
        /// <param name="cullFrom">Optional anchor the <paramref name="maxDistance"/> ring is measured from. Defaults to
        /// null = the camera eye.</param>
        public static bool Draw(SpriteBatch batch, SpriteFont font, IIsoCamera3D camera, Vector3 worldPos,
            Vector3 offset, string text, Color color, IDesignViewport designViewport,
            float scale = 1f, float maxDistance = 0f, Vector3? cullFrom = null)
        {
            if (batch is null || font is null || camera is null || designViewport is null || string.IsNullOrEmpty(text)) return false;
            Vector3 cullOrigin = cullFrom ?? camera.Eye;
            if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
            if (!NameplateAnchorProjection.Project(camera, worldPos, offset, designViewport, out Vector2 pixel)) return false;

            DrawAt(batch, font, pixel, text, color, scale);
            return true;
        }

        // Shared draw body once the anchor pixel (framebuffer or design space, whichever the caller's overload
        // resolved) is known: centres the text horizontally on it and bottom-anchors it there, so it floats above
        // the point.
        static void DrawAt(SpriteBatch batch, SpriteFont font, Vector2 pixel, string text, Color color, float scale)
        {
            Vector2 size = font.Measure(text) * scale;
            var topLeft = new Vector2(pixel.X - size.X * 0.5f, pixel.Y - size.Y);
            batch.DrawString(font, text, topLeft, color, scale);
        }

        /// <summary>
        /// The distance cull predicate, factored out of <see cref="Draw(SpriteBatch, SpriteFont, IIsoCamera3D, Vector3, Vector3, string, Color, int, int, float, float, Vector3?)"/> so it is headless-testable (Draw itself
        /// needs a GPU <see cref="SpriteBatch"/>). Returns <c>true</c> when the label should be culled: when
        /// <paramref name="maxDistance"/> &gt; 0 and <paramref name="worldPos"/> is farther than that from
        /// <paramref name="cullFrom"/>. A <paramref name="maxDistance"/> of 0 (or less) never culls.
        /// </summary>
        public static bool ShouldCull(Vector3 worldPos, Vector3 cullFrom, float maxDistance)
            => maxDistance > 0f && Vector3.DistanceSquared(worldPos, cullFrom) > maxDistance * maxDistance;
    }
}
