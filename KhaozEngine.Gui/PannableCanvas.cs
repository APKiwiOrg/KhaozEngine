using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A generic pannable viewport over world-space content larger than a caller-supplied viewport.
    /// Drag and wheel pan (wheel = vertical pan), clamps to caller-supplied content bounds plus padding,
    /// scissor-clips rendering, and exposes world/screen transforms plus a click-through-safe tap helper.
    /// No game-specific concepts.
    ///
    /// <para>Delegates its transform / clamp / pan math to a backing <see cref="Render2D.Camera2D"/>, so
    /// the gesture math has a single implementation. Per frame: set <see cref="Viewport"/> and
    /// <see cref="ContentBounds"/>, call <see cref="Update"/> to pan/clamp, then <see cref="Draw"/> with a
    /// world-space draw callback. Query <see cref="TryGetTap"/> for the world point(s) tapped this frame.</para>
    ///
    /// <para>This release is pan-only (drag + wheel). Two-finger pinch zoom is a follow-up.
    /// <see cref="MinZoom"/>/<see cref="MaxZoom"/> still bound <see cref="Focus"/>.</para>
    /// </summary>
    public sealed class PannableCanvas
    {
        readonly Camera2D _camera = new();

        /// <summary>The viewport rectangle in design/screen coordinates. Set each frame.</summary>
        public Rect Viewport { get; set; }

        /// <summary>The raw content extent in world coordinates, used (inflated by <see cref="Padding"/>) for clamping. Set each frame.</summary>
        public Rect ContentBounds { get; set; }

        /// <summary>Extra slack in world units added on all sides of <see cref="ContentBounds"/> before clamping.</summary>
        public float Padding { get; set; }

        /// <summary>World units panned per unit of wheel-scroll delta (vertical).</summary>
        public float ScrollPanSpeed { get; set; } = 0.5f;

        /// <summary>When true, <see cref="Update"/> reserves the viewport via <see cref="Pointer.BlockRegion"/> so lower screens ignore drags/scrolls that start inside it.</summary>
        public bool BlockInput { get; set; } = true;

        /// <summary>When false, all panning is ignored: drag and wheel scroll pan.</summary>
        public bool EnablePan { get; set; } = true;

        /// <summary>Smallest allowed camera zoom (bounds <see cref="Focus"/>).</summary>
        public float MinZoom { get; set; } = 0.1f;

        /// <summary>Largest allowed camera zoom (bounds <see cref="Focus"/>).</summary>
        public float MaxZoom { get; set; } = 10f;

        /// <summary>The backing camera. Exposed so callers can read or drive position/zoom directly;
        /// direct writes bypass clamping, so call <see cref="Update"/> (which clamps) afterward to keep the view in bounds.</summary>
        public Camera2D Camera => _camera;

        /// <summary>Maps a world point to design/screen coordinates (accounts for the <see cref="Viewport"/> offset).</summary>
        public Vector2 WorldToScreen(Vector2 world) =>
            _camera.WorldToScreen(world, (int)Viewport.Width, (int)Viewport.Height)
            + new Vector2(Viewport.X, Viewport.Y);

        /// <summary>Maps a design/screen point back to world coordinates (inverse of <see cref="WorldToScreen"/>).</summary>
        public Vector2 ScreenToWorld(Vector2 screen) =>
            _camera.ScreenToWorld(screen - new Vector2(Viewport.X, Viewport.Y), (int)Viewport.Width, (int)Viewport.Height);

        /// <summary>Centres the camera so <paramref name="world"/> sits at the viewport centre, then clamps.</summary>
        public void CenterOn(Vector2 world)
        {
            _camera.CenterOn(world);
            Clamp();
        }

        /// <summary>Frames <paramref name="worldRect"/>: fits <see cref="Camera"/> zoom so the rect
        /// (optionally inflated by <paramref name="paddingFraction"/> on each side) is fully visible - a
        /// contain fit clamped to <see cref="MinZoom"/>/<see cref="MaxZoom"/> - centres on it, then clamps
        /// to <see cref="ContentBounds"/>. (Unlike <see cref="CenterOn"/>, this also changes the zoom.)</summary>
        public void Focus(Rect worldRect, float paddingFraction = 0f)
        {
            _camera.Focus(worldRect, (int)Viewport.Width, (int)Viewport.Height, paddingFraction, MinZoom, MaxZoom);
            Clamp();
        }

        /// <summary>Centres the camera on the middle of <see cref="ContentBounds"/>, then clamps. The typical on-open default.</summary>
        public void CenterContent() =>
            CenterOn(new Vector2(ContentBounds.X + ContentBounds.Width / 2f, ContentBounds.Y + ContentBounds.Height / 2f));

        /// <summary>Reserves the viewport (if <see cref="BlockInput"/>), pans on drag and wheel, then clamps.
        /// Call once per frame before drawing. Pass <c>InputState.ScrollDelta</c> for <paramref name="wheelDelta"/>.</summary>
        public void Update(Pointer pointer, float wheelDelta)
        {
            if (BlockInput) pointer.BlockRegion(Viewport);

            if (EnablePan)
            {
                _camera.PanByScreenDelta(pointer.GetDragDelta(Viewport));

                if (wheelDelta != 0f && pointer.IsPointerIn(Viewport))
                    _camera.Position += new Vector2(0f, -wheelDelta * ScrollPanSpeed / _camera.Zoom);
            }

            Clamp();
        }

        /// <summary>The given pointer's position in world coordinates (for hover highlighting).</summary>
        public Vector2 PointerWorld(Pointer pointer) => ScreenToWorld(pointer.Position);

        /// <summary>
        /// True on the frame the viewport was tapped (press-origin and release both inside it). Returns the
        /// press and release world points so the caller can hit-test both and require the same target; a pan
        /// that ends inside returns true too, but its press/release world points differ so the check rejects it.
        /// When not a tap, both out-params are <c>default</c>.
        /// </summary>
        public bool TryGetTap(Pointer pointer, out Vector2 pressWorld, out Vector2 releaseWorld)
        {
            if (pointer.IsJustReleased
                && Viewport.Contains(pointer.PressOrigin)
                && Viewport.Contains(pointer.Position))
            {
                pressWorld = ScreenToWorld(pointer.PressOrigin);
                releaseWorld = ScreenToWorld(pointer.Position);
                return true;
            }

            pressWorld = default;
            releaseWorld = default;
            return false;
        }

        /// <summary>
        /// Scissor-clips to the viewport and invokes <paramref name="drawWorld"/> inside a batch whose camera
        /// transform maps world coordinates to the viewport. The caller draws in world coordinates inside
        /// <paramref name="drawWorld"/>.
        /// </summary>
        public void Draw(SpriteBatch batch, Action drawWorld)
        {
            batch.SetScissor(Viewport);
            batch.Begin(_camera);
            drawWorld();
            batch.End();
            batch.ClearScissor();
        }

        Rect PaddedBounds => new(
            ContentBounds.X - Padding, ContentBounds.Y - Padding,
            ContentBounds.Width + Padding * 2f, ContentBounds.Height + Padding * 2f);

        void Clamp() =>
            _camera.Position = _camera.ClampPosition(_camera.Position, PaddedBounds, (int)Viewport.Width, (int)Viewport.Height);
    }
}
