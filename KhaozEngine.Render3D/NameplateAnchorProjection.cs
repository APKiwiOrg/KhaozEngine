using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Composes the nameplate/world-label screen anchor from two projections instead of one, to fix the
    /// perspective lean: a world-vertical offset (e.g. <c>(0, headHeight, 0)</c>) projects screen-vertically
    /// only when the entity sits on the camera's central vertical plane. Off that plane, a perspective camera's
    /// <see cref="IIsoCamera3D.WorldToScreen"/> leans the head-point pixel horizontally away from the entity's
    /// own body column (zero lean at screen centre, the sign flipping as the entity crosses that centre
    /// plane), so projecting <c>worldPos + offset</c> alone puts the plate beside the entity instead of above
    /// it, and swaps side as the camera orbits.
    /// </summary>
    /// <remarks>
    /// The fix: project the head point (<c>worldPos + offset</c>) for its screen Y only, and the body column
    /// (<c>worldPos</c> plus just the lateral part of the offset, i.e. <c>offset.X</c>/<c>offset.Z</c> with the
    /// vertical component dropped) for its screen X. The body column tracks the entity's actual screen
    /// position, so the plate hangs screen-above the visible body at every screen position instead of beside
    /// it, while the head point still drives the float height, so it keeps tracking the entity's height and
    /// distance from the camera. A lateral offset component is a caller's deliberate world-space nudge (not
    /// the vertical float height), so it stays folded into the body column's X projection rather than being
    /// dropped, keeping a caller-side lateral offset meaningful.
    /// </remarks>
    internal static class NameplateAnchorProjection
    {
        /// <summary>
        /// Projects the composed anchor pixel for a nameplate/world-label. Returns <c>false</c> with
        /// <paramref name="pixel"/> = <c>default</c> exactly when the head point (<paramref name="worldPos"/> +
        /// <paramref name="offset"/>) does not project (the existing cull: behind the camera, or outside the
        /// depth range) - unchanged from before this fix. When the head point projects but the body column
        /// (the steep-edge case where the head clears the near plane but the feet do not) does not, the head
        /// pixel is used alone rather than failing the whole anchor.
        /// </summary>
        /// <param name="camera">The camera whose projection places the anchor.</param>
        /// <param name="worldPos">The world anchor the caller passed in (e.g. the entity's feet/centre).</param>
        /// <param name="offset">World-space offset added before projecting (e.g. <c>(0, headHeight, 0)</c> to
        /// float above the head). Only its lateral (X/Z) part feeds the body-column projection that drives
        /// screen X. The full offset (including the vertical part) still drives screen Y via the head point.</param>
        /// <param name="viewportWidth">Framebuffer width in pixels.</param>
        /// <param name="viewportHeight">Framebuffer height in pixels.</param>
        /// <param name="pixel">The composed anchor pixel (body-column X, head-point Y), or <c>default</c> when
        /// culled.</param>
        /// <returns><c>true</c> if the head point projects (drawable), <c>false</c> if culled.</returns>
        internal static bool Project(IIsoCamera3D camera, Vector3 worldPos, Vector3 offset, int viewportWidth,
            int viewportHeight, out Vector2 pixel)
        {
            if (!camera.WorldToScreen(worldPos + offset, viewportWidth, viewportHeight, out Vector2 topPixel))
            {
                pixel = default;
                return false;
            }

            Vector3 basePoint = worldPos + new Vector3(offset.X, 0f, offset.Z);
            pixel = camera.WorldToScreen(basePoint, viewportWidth, viewportHeight, out Vector2 basePixel)
                ? new Vector2(basePixel.X, topPixel.Y)
                : topPixel;
            return true;
        }
    }
}
