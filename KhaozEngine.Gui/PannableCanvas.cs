using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>Whether <see cref="PannableCanvas.Update"/> applies the mouse wheel as a vertical pan or a zoom.</summary>
    public enum CanvasWheelMode
    {
        /// <summary>Wheel pans the view vertically (the historical default; back-compat).</summary>
        Pan,

        /// <summary>Wheel zooms the view toward the pointer, clamped to <see cref="PannableCanvas.MinZoom"/>/<see cref="PannableCanvas.MaxZoom"/>.</summary>
        Zoom,
    }

    /// <summary>
    /// A generic pannable viewport over world-space content larger than a caller-supplied viewport.
    /// Drag pans; the wheel either pans vertically or zooms toward the pointer per <see cref="Wheel"/>
    /// (default <see cref="CanvasWheelMode.Pan"/>). Clamps to caller-supplied content bounds plus padding,
    /// scissor-clips rendering, and exposes world/screen transforms plus a click-through-safe tap helper.
    /// No game-specific concepts.
    ///
    /// <para>Delegates its transform / clamp / pan / zoom math to a backing <see cref="Render2D.Camera2D"/>, so
    /// the gesture math has a single implementation. Per frame: set <see cref="Viewport"/> and
    /// <see cref="ContentBounds"/>, call <see cref="Update"/> to pan/zoom/clamp, then <see cref="Draw"/> with a
    /// world-space draw callback. Query <see cref="TryGetTap"/> for the world point(s) tapped this frame.</para>
    ///
    /// <para>Wheel zoom is opt-in via <see cref="Wheel"/>. Two-finger pinch zoom is a follow-up.
    /// <see cref="MinZoom"/>/<see cref="MaxZoom"/> bound both <see cref="Focus"/> and wheel zoom.
    /// Note: bitmap <c>SpriteFont</c> text is baked at a fixed size, so world-space text blurs at any
    /// non-1.0 zoom. A caller that needs crisp text either pins <see cref="MinZoom"/> = <see cref="MaxZoom"/> = 1
    /// (no zoom) or lists a few whole-number-ish stops in <see cref="SnapZoomLevels"/> and accepts blur only
    /// at those stops.</para>
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

        /// <summary>Whether the wheel pans vertically or zooms toward the pointer. Default <see cref="CanvasWheelMode.Pan"/> (back-compat).</summary>
        public CanvasWheelMode Wheel { get; set; } = CanvasWheelMode.Pan;

        /// <summary>Multiplicative zoom applied per unit of wheel delta when <see cref="Wheel"/> is <see cref="CanvasWheelMode.Zoom"/>:
        /// each frame <c>Zoom *= ZoomStep^wheelDelta</c> (so wheel-up zooms in, wheel-down zooms out). Ignored when
        /// <see cref="SnapZoomLevels"/> is set. Must be &gt; 1.</summary>
        public float ZoomStep { get; set; } = 1.1f;

        /// <summary>When set (non-empty) and <see cref="Wheel"/> is <see cref="CanvasWheelMode.Zoom"/>, the wheel snaps to
        /// these discrete zoom stops instead of stepping continuously by <see cref="ZoomStep"/>: each wheel event moves
        /// to the adjacent stop in the scroll direction (magnitude does not skip stops). Lets a caller keep bitmap text
        /// crisp at a few chosen zoom levels. Stops need not be sorted; each is still clamped to
        /// <see cref="MinZoom"/>/<see cref="MaxZoom"/>.</summary>
        public float[]? SnapZoomLevels { get; set; }

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

        /// <summary>Reserves the viewport (if <see cref="BlockInput"/>), pans on drag, applies the wheel per
        /// <see cref="Wheel"/> (vertical pan or zoom-toward-pointer), then clamps. Call once per frame before
        /// drawing. Pass <c>InputState.ScrollDelta</c> for <paramref name="wheelDelta"/>.</summary>
        public void Update(Pointer pointer, float wheelDelta)
        {
            if (BlockInput) pointer.BlockRegion(Viewport);

            if (EnablePan)
            {
                _camera.PanByScreenDelta(pointer.GetDragDelta(Viewport));

                if (wheelDelta != 0f && pointer.IsPointerIn(Viewport))
                {
                    if (Wheel == CanvasWheelMode.Zoom)
                        ZoomTowardCursor(wheelDelta, pointer.Position);
                    else
                        _camera.Position += new Vector2(0f, -wheelDelta * ScrollPanSpeed / _camera.Zoom);
                }
            }

            Clamp();
        }

        /// <summary>Applies a wheel zoom step (continuous <see cref="ZoomStep"/> or a <see cref="SnapZoomLevels"/> stop),
        /// clamped to <see cref="MinZoom"/>/<see cref="MaxZoom"/>, keeping the world point under
        /// <paramref name="cursorScreen"/> fixed. The trailing <see cref="Clamp"/> in <see cref="Update"/> re-clamps
        /// position to <see cref="ContentBounds"/>.</summary>
        void ZoomTowardCursor(float wheelDelta, Vector2 cursorScreen)
        {
            float target = Math.Clamp(TargetZoom(wheelDelta), MinZoom, MaxZoom);
            if (target == _camera.Zoom) return;

            // Keep the cursor's world point fixed: adjust position by how far that point drifts under the new zoom.
            var worldBefore = ScreenToWorld(cursorScreen);
            _camera.Zoom = target;
            var worldAfter = ScreenToWorld(cursorScreen);
            _camera.Position += worldBefore - worldAfter;
        }

        /// <summary>The desired zoom for a wheel event before min/max clamping: the next
        /// <see cref="SnapZoomLevels"/> stop in the scroll direction when set, else <c>Zoom * ZoomStep^wheelDelta</c>.</summary>
        float TargetZoom(float wheelDelta)
        {
            if (SnapZoomLevels is { Length: > 0 } levels)
                return NextSnap(_camera.Zoom, wheelDelta, levels);

            return _camera.Zoom * MathF.Pow(ZoomStep, wheelDelta);
        }

        /// <summary>The stop in <paramref name="levels"/> adjacent to <paramref name="current"/> in the scroll
        /// direction (up when <paramref name="wheelDelta"/> &gt; 0, down otherwise). Returns <paramref name="current"/>
        /// unchanged when no stop lies further in that direction. Order-independent.</summary>
        static float NextSnap(float current, float wheelDelta, float[] levels)
        {
            const float eps = 1e-4f;
            float best = current;
            bool found = false;

            if (wheelDelta > 0f)
            {
                foreach (var l in levels)
                    if (l > current + eps && (!found || l < best)) { best = l; found = true; }
            }
            else
            {
                foreach (var l in levels)
                    if (l < current - eps && (!found || l > best)) { best = l; found = true; }
            }

            return found ? best : current;
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
