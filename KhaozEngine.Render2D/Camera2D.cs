using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A 2D camera: <see cref="Position"/> is the world point shown at the centre of the viewport,
    /// with <see cref="Zoom"/> (>1 = zoomed in) and <see cref="Rotation"/> (radians). Pure System.Numerics,
    /// headless, no GPU. Produces the view-projection a <see cref="SpriteBatch"/> uploads.
    /// </summary>
    public sealed class Camera2D
    {
        public Vector2 Position = Vector2.Zero;
        public float Zoom = 1f;
        public float Rotation = 0f;

        /// <summary>World -> screen-pixel transform (top-left origin, y-down).</summary>
        public Matrix4x4 GetView(int viewportWidth, int viewportHeight)
        {
            var center = new Vector3(viewportWidth * 0.5f, viewportHeight * 0.5f, 0f);
            return Matrix4x4.CreateTranslation(-Position.X, -Position.Y, 0f)
                 * Matrix4x4.CreateRotationZ(-Rotation)
                 * Matrix4x4.CreateScale(Zoom, Zoom, 1f)
                 * Matrix4x4.CreateTranslation(center);
        }

        /// <summary>World -> clip transform (the matrix the sprite batch uses).</summary>
        public Matrix4x4 GetViewProjection(int viewportWidth, int viewportHeight)
        {
            // Authored y-down ortho (right way up on Metal/D3D). The backend clip-space-Y correction is applied
            // by SpriteBatch where it bakes vertices into clip space (via GpuClip, derived from the live device),
            // so this stays the authored matrix - keeping it consistent with the CPU world/screen helpers below.
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return GetView(viewportWidth, viewportHeight) * ortho;
        }

        public Vector2 WorldToScreen(Vector2 world, int viewportWidth, int viewportHeight)
        {
            var v = Vector2.Transform(world, GetView(viewportWidth, viewportHeight));
            return v;
        }

        /// <summary>
        /// Screen-pixel -> world transform, the inverse of <see cref="WorldToScreen"/>.
        /// <para>
        /// A degenerate camera has no inverse and yields <see cref="Position"/> (a defined answer) instead of
        /// NaN. <see cref="Matrix4x4.Invert"/> does not throw on a singular matrix: it fills the result with
        /// NaN and returns false, and that NaN used to flow straight out of here into whatever consumed the
        /// unpicked world position (mouse picking, hit-testing, a camera-follow calculation). The only way to
        /// get there is a <see cref="Zoom"/> of exactly 0, which collapses the whole viewport onto
        /// <see cref="Position"/>, so <see cref="Position"/> is the world point every screen pixel then maps
        /// back to. A NEGATIVE zoom is a mirror rather than a degeneracy and converts exactly, matching
        /// <see cref="PanByScreenDelta"/>'s guard being about the collapse and not about the sign.
        /// </para>
        /// <para>Call <see cref="TryScreenToWorld"/> instead when the caller wants to KNOW the camera was
        /// degenerate rather than absorb it.</para>
        /// </summary>
        public Vector2 ScreenToWorld(Vector2 screen, int viewportWidth, int viewportHeight)
            => TryScreenToWorld(screen, viewportWidth, viewportHeight, out Vector2 world) ? world : Position;

        /// <summary>
        /// As <see cref="ScreenToWorld"/>, but reports whether the conversion was real. Returns false for a
        /// camera whose view matrix cannot be inverted (see <see cref="ScreenToWorld"/>) or whose transform
        /// produced a non-finite result, and writes the <see cref="Position"/> fallback to
        /// <paramref name="world"/> either way, so a caller that does not care can ignore the bool.
        /// </summary>
        public bool TryScreenToWorld(Vector2 screen, int viewportWidth, int viewportHeight, out Vector2 world)
        {
            if (!Matrix4x4.Invert(GetView(viewportWidth, viewportHeight), out Matrix4x4 inv))
            {
                world = Position;
                return false;
            }

            // A non-finite Zoom/Rotation/Position makes the determinant NaN, which Invert's near-zero test does
            // not catch (a NaN comparison is false), so it reports success over an all-NaN matrix. Check the
            // result rather than trusting the bool alone.
            Vector2 v = Vector2.Transform(screen, inv);
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y))
            {
                world = Position;
                return false;
            }

            world = v;
            return true;
        }

        /// <summary>Sets <see cref="Position"/> so <paramref name="world"/> sits at the viewport centre.
        /// (Position already is the world point at centre, so this is an explicit alias for readability
        /// and API parity with the pannable canvas.)</summary>
        public void CenterOn(Vector2 world) => Position = world;

        /// <summary>Moves the camera so world content tracks a screen drag of <paramref name="screenDelta"/>:
        /// the world moves by <c>screenDelta / Zoom</c>, applied opposite to the drag (grab-and-drag).
        /// No-op for a zero delta or a non-positive <see cref="Zoom"/>.</summary>
        public void PanByScreenDelta(Vector2 screenDelta)
        {
            if (screenDelta == Vector2.Zero || Zoom <= 0f) return;
            Position -= screenDelta / Zoom;
        }

        /// <summary>
        /// Frames <paramref name="worldRect"/>: sets <see cref="Zoom"/> so the rect (optionally inflated
        /// by <paramref name="paddingFraction"/> on each side) is fully visible - a contain fit,
        /// <c>min(viewportWidth / rectWidth, viewportHeight / rectHeight)</c> - clamped to
        /// <paramref name="minZoom"/>/<paramref name="maxZoom"/>, then centres <see cref="Position"/> on the
        /// rect. Pure and headless; ignores <see cref="Rotation"/> like the other axis-aligned helpers. Does
        /// not clamp to world bounds - call <see cref="ClampPosition(System.Numerics.Vector2, KhaozEngine.Primitives.Rect, int, int)"/> after if the rect is a sub-region.
        /// </summary>
        public void Focus(Rect worldRect, int viewportWidth, int viewportHeight, float paddingFraction = 0f,
            float minZoom = 0.0001f, float maxZoom = float.MaxValue)
        {
            float scale = 1f + 2f * paddingFraction;
            float w = MathF.Max(1f, worldRect.Width * scale);
            float h = MathF.Max(1f, worldRect.Height * scale);
            float fit = ViewportMath.Fit(w, h, viewportWidth, viewportHeight);

            Zoom = Math.Clamp(fit, minZoom, maxZoom);
            Position = new Vector2(worldRect.X + worldRect.Width / 2f, worldRect.Y + worldRect.Height / 2f);
        }

        /// <summary>
        /// Returns <paramref name="desired"/> clamped so the visible world rectangle
        /// (viewport size divided by <see cref="Zoom"/>) stays inside <paramref name="worldBounds"/>.
        /// On an axis where the world is smaller than the view, the result is centred on that axis. Does
        /// not mutate <see cref="Position"/>; the caller assigns the result if wanted. Ignores
        /// <see cref="Rotation"/> (exact when it is 0); requires <see cref="Zoom"/> &gt; 0.
        /// </summary>
        public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight)
            => ClampPosition(desired, worldBounds, viewportWidth, viewportHeight, Zoom);

        /// <summary>
        /// As <see cref="ClampPosition(Vector2, Rect, int, int)"/> but clamps for an explicit
        /// <paramref name="zoom"/> instead of <see cref="Zoom"/>. Used when framing for a zoom the camera has
        /// not eased to yet (e.g. a room hand-off targeting the next room's zoom). Requires <paramref name="zoom"/> &gt; 0.
        /// </summary>
        public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight, float zoom)
        {
            float halfW = viewportWidth / (2f * zoom);
            float halfH = viewportHeight / (2f * zoom);

            float x = worldBounds.Width >= 2f * halfW
                ? Math.Clamp(desired.X, worldBounds.X + halfW, worldBounds.Right - halfW)
                : worldBounds.X + worldBounds.Width / 2f;

            float y = worldBounds.Height >= 2f * halfH
                ? Math.Clamp(desired.Y, worldBounds.Y + halfH, worldBounds.Bottom - halfH)
                : worldBounds.Y + worldBounds.Height / 2f;

            return new Vector2(x, y);
        }
    }
}
