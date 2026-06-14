using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;
using KhaozEngine.Graphics;
using XnaViewport = Microsoft.Xna.Framework.Graphics.Viewport;

namespace KhaozEngine.UI;

/// <summary>
/// A generic pannable viewport over world-space content larger than a caller-supplied viewport.
/// Drag and wheel pan (wheel = vertical pan), optional two-finger pinch zoom, clamps to caller-supplied
/// content bounds plus padding, scissor-clips rendering, and exposes world/screen transforms plus a
/// click-through-safe tap helper. No game-specific concepts.
///
/// <para>Delegates its transform / clamp / pan / zoom / tap math to a backing
/// <see cref="KhaozEngine.Graphics.Camera2D"/> (shared with
/// <see cref="KhaozEngine.Graphics.CameraController"/>), so the gesture math has a single
/// implementation. <see cref="CameraOffset"/> is the legacy additive-offset view of the camera
/// (<c>-Position * Zoom</c>).</para>
///
/// Per frame: set <see cref="Viewport"/> and <see cref="ContentBounds"/>, call Update to pan/zoom/clamp,
/// then Draw with a world-space draw callback. Query TryGetTap for the world point(s) tapped this frame.
/// </summary>
public sealed class PannableCanvas
{
    private readonly InputManager _input;
    private readonly Camera2D _camera = new();
    private readonly PinchGestureTracker _pinch = new();
    private RasterizerState? _scissorRasterizer;

    /// <summary>Creates a pannable canvas bound to an input source.</summary>
    public PannableCanvas(InputManager input) => _input = input;

    /// <summary>The viewport rectangle in virtual screen coordinates. Set each frame.</summary>
    public Rectangle Viewport { get; set; }

    /// <summary>The raw content extent in world coordinates, used (inflated by <see cref="Padding"/>) for clamping. Set each frame.</summary>
    public Rectangle ContentBounds { get; set; }

    /// <summary>Extra slack in world units added on all sides of <see cref="ContentBounds"/> before clamping.</summary>
    public int Padding { get; set; }

    /// <summary>World units panned per unit of wheel-scroll delta (vertical).</summary>
    public float ScrollPanSpeed { get; set; } = 0.5f;

    /// <summary>When true, Update reserves the viewport via <c>InputManager.BlockInputRegion</c> so lower screens ignore drags/scrolls that start inside it.</summary>
    public bool BlockInput { get; set; } = true;

    /// <summary>When false, all panning is ignored: drag, two-finger pan, and wheel scroll pan.</summary>
    public bool EnablePan { get; set; } = true;

    /// <summary>When false, pinch zoom is ignored (the canvas stays at its current zoom; wheel still pans).</summary>
    public bool EnableZoom { get; set; } = true;

    /// <summary>Smallest allowed camera zoom (pinch zoom-out clamps here).</summary>
    public float MinZoom { get; set; } = 0.1f;

    /// <summary>Largest allowed camera zoom (pinch zoom-in clamps here).</summary>
    public float MaxZoom { get; set; } = 10f;

    /// <summary>The backing camera. Exposed so callers can read or drive position/zoom directly;
    /// direct writes bypass clamping, so call <see cref="Update"/> (which clamps) afterward to keep the view in bounds.</summary>
    public Camera2D Camera => _camera;

    /// <summary>The current camera offset (legacy additive pan state): <c>-Position * Zoom</c>. Read-only; change it via panning or the focus helpers.</summary>
    public Vector2 CameraOffset => -_camera.Position * _camera.Zoom;

    private XnaViewport CameraViewport => new(Viewport.X, Viewport.Y, Viewport.Width, Viewport.Height);

    /// <summary>Maps a world point to virtual screen coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 world) => _camera.WorldToScreen(world, CameraViewport);

    /// <summary>Maps a virtual screen point back to world coordinates (inverse of <see cref="WorldToScreen"/>).</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => _camera.ScreenToWorld(screen, CameraViewport);

    /// <summary>Centers the camera so <paramref name="world"/> sits at the viewport center, then clamps.</summary>
    public void CenterOn(Vector2 world)
    {
        _camera.CenterOn(world);
        Clamp();
    }

    /// <summary>Frames <paramref name="worldRect"/>: fits <see cref="Camera"/> zoom so the rect
    /// (optionally inflated by <paramref name="paddingFraction"/> on each side) is fully visible —
    /// a contain fit clamped to <see cref="MinZoom"/>/<see cref="MaxZoom"/> — centers on it, then clamps
    /// to <see cref="ContentBounds"/>. Delegates to
    /// <see cref="KhaozEngine.Graphics.Camera2D.Focus(Rectangle, Viewport, float, float, float)"/>,
    /// the shared fit-to-rect core. (Unlike <see cref="CenterOn"/>, this also changes the zoom.)</summary>
    public void Focus(Rectangle worldRect, float paddingFraction = 0f)
    {
        _camera.Focus(worldRect, CameraViewport, paddingFraction, MinZoom, MaxZoom);
        Clamp();
    }

    /// <summary>Centers the camera on the middle of <see cref="ContentBounds"/>, then clamps. The typical on-open default.</summary>
    public void CenterContent() =>
        CenterOn(new Vector2(ContentBounds.X + ContentBounds.Width / 2f, ContentBounds.Y + ContentBounds.Height / 2f));

    /// <summary>Reserves the viewport (if <see cref="BlockInput"/>), pans on drag and wheel, zooms on pinch, then clamps. Call once per frame before drawing.</summary>
    public void Update()
    {
        if (BlockInput) _input.BlockInputRegion(Viewport);

        if (_input.TryGetPinch(out Pinch pinch))
        {
            _pinch.Apply(_camera, pinch, CameraViewport, EnablePan, EnableZoom, MinZoom, MaxZoom);
        }
        else
        {
            _pinch.Reset();

            if (EnablePan)
            {
                _camera.PanByScreenDelta(_input.GetDragDelta(Viewport));

                int scroll = _input.GetScrollIn(Viewport);
                if (scroll != 0)
                    _camera.Position += new Vector2(0f, -scroll * ScrollPanSpeed / _camera.Zoom);
            }
        }

        Clamp();
    }

    /// <summary>The current pointer position in world coordinates (for hover highlighting).</summary>
    public Vector2 PointerWorld => ScreenToWorld(_input.PointerPosition);

    /// <summary>
    /// True on the frame the viewport was tapped (press-origin and release both inside it). Returns the
    /// press and release world points so the caller can hit-test both and require the same target; a pan
    /// that ends inside returns true too, but its press/release world points differ so the check rejects it.
    /// </summary>
    public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld) =>
        CameraGestures.TryGetTap(_input, _camera, CameraViewport, out pressWorld, out releaseWorld);

    /// <summary>
    /// Scissor-clips to the viewport and invokes <paramref name="drawWorld"/> with a SpriteBatch whose
    /// transform maps world coordinates -> virtual screen -> physical pixels. Pass <c>vr.Scale</c> and
    /// <c>vr.ScaleMatrix</c> for <paramref name="renderScale"/> / <paramref name="scaleMatrix"/>.
    /// </summary>
    public void Draw(SpriteBatch sb, GraphicsDevice gd, float renderScale, Matrix scaleMatrix, Action drawWorld)
    {
        _scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };

        gd.ScissorRectangle = new Rectangle(
            (int)(Viewport.X * renderScale),
            (int)(Viewport.Y * renderScale),
            Math.Max(0, (int)(Viewport.Width * renderScale)),
            Math.Max(0, (int)(Viewport.Height * renderScale)));

        Matrix world = _camera.GetViewMatrix(CameraViewport);

        sb.Begin(samplerState: SamplerState.PointClamp,
                 rasterizerState: _scissorRasterizer,
                 transformMatrix: world * scaleMatrix);
        drawWorld();
        sb.End();
    }

    private Rectangle PaddedBounds => new(
        ContentBounds.X - Padding, ContentBounds.Y - Padding,
        ContentBounds.Width + Padding * 2, ContentBounds.Height + Padding * 2);

    private void Clamp() =>
        _camera.Position = _camera.ClampPosition(_camera.Position, PaddedBounds, CameraViewport);
}
