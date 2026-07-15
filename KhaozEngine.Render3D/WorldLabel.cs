using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Draws a string anchored to a world point as a screen-space label - the centralized nameplate / world-text
    /// helper (e.g. a player name floating above their head). It projects <c>worldPos + offset</c> through the
    /// camera's <see cref="IIsoCamera3D.WorldToScreen"/>, then draws <c>text</c> centred horizontally and lifted so
    /// it sits above that pixel, via <see cref="SpriteBatch.DrawString(SpriteFont,string,Vector2,Color,float)"/>.
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
        /// <c>(0, headHeight, 0)</c> to float the name above the head).</param>
        /// <param name="text">The label text (empty/null is a no-op).</param>
        /// <param name="color">Text color (RGBA).</param>
        /// <param name="viewportWidth">Framebuffer width in pixels.</param>
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
            if (!camera.WorldToScreen(worldPos + offset, viewportWidth, viewportHeight, out Vector2 pixel)) return false;

            Vector2 size = font.Measure(text) * scale;
            // Centre horizontally on the projected pixel; anchor the text's BOTTOM there so it floats above the point.
            var topLeft = new Vector2(pixel.X - size.X * 0.5f, pixel.Y - size.Y);
            batch.DrawString(font, text, topLeft, color, scale);
            return true;
        }

        /// <summary>
        /// The distance cull predicate, factored out of <see cref="Draw"/> so it is headless-testable (Draw itself
        /// needs a GPU <see cref="SpriteBatch"/>). Returns <c>true</c> when the label should be culled: when
        /// <paramref name="maxDistance"/> &gt; 0 and <paramref name="worldPos"/> is farther than that from
        /// <paramref name="cullFrom"/>. A <paramref name="maxDistance"/> of 0 (or less) never culls.
        /// </summary>
        public static bool ShouldCull(Vector3 worldPos, Vector3 cullFrom, float maxDistance)
            => maxDistance > 0f && Vector3.DistanceSquared(worldPos, cullFrom) > maxDistance * maxDistance;
    }
}
