using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.Graphics;

/// <summary>
/// Drives a <see cref="Camera2D"/> from an <see cref="InputManager"/>: drag / two-finger pan,
/// scroll-wheel and pinch zoom (about the cursor / pinch focus), and a world-bounds clamp. Reusable
/// for any world render (place a tower on a tap, pan the battlefield on a drag); the
/// <c>PannableCanvas</c>-style <see cref="TryGetTap"/> lets a caller tell a tap from a pan.
///
/// <para>Call <see cref="Update"/> once per frame with the current viewport and the world rectangle
/// the view must stay inside. The viewport is passed explicitly (like <see cref="Camera2D"/>'s
/// transform methods take a <see cref="Viewport"/>), so the controller is headless: no
/// <c>GraphicsDevice</c> is required and the step is unit-testable. The controller owns no matrix
/// math of its own; it reuses <see cref="Camera2D.ScreenToWorld(Vector2, Viewport)"/> and
/// <see cref="Camera2D.ClampPosition(Vector2, Rectangle, Viewport)"/>.</para>
/// </summary>
public sealed class CameraController
{
    private readonly InputManager _input;
    private readonly Camera2D _camera;

    private Viewport _lastViewport;
    private readonly PinchGestureTracker _pinch = new();

    /// <summary>Creates a controller bound to an input source and the camera it drives.</summary>
    public CameraController(InputManager input, Camera2D camera)
    {
        _input = input;
        _camera = camera;
    }

    /// <summary>The camera this controller drives.</summary>
    public Camera2D Camera => _camera;

    /// <summary>Smallest allowed <see cref="Camera2D.Zoom"/>. Zoom-out clamps here.</summary>
    public float MinZoom { get; set; } = 0.1f;

    /// <summary>Largest allowed <see cref="Camera2D.Zoom"/>. Zoom-in clamps here.</summary>
    public float MaxZoom { get; set; } = 10f;

    /// <summary>Multiplicative zoom factor applied per 120-unit wheel notch (a fractional/multi-notch
    /// delta scales smoothly via a power). 1.1 ~= a gentle 10% per notch.</summary>
    public float WheelZoomStep { get; set; } = 1.1f;

    /// <summary>When false, drag / two-finger pan is ignored (e.g. a locked or follow-cam mode).</summary>
    public bool EnablePan { get; set; } = true;

    /// <summary>When false, scroll-wheel and pinch zoom are ignored.</summary>
    public bool EnableZoom { get; set; } = true;

    /// <summary>When true, reserves the viewport via <c>InputManager.BlockInputRegion</c> each frame so
    /// lower screens ignore drags/scrolls that start inside it. Off by default (a gameplay camera
    /// usually owns the whole screen); turn on when the view is an inset region over other content.</summary>
    public bool BlockInput { get; set; }

    /// <summary>
    /// Consumes this frame's gestures and updates the camera: pan (drag or two-finger), zoom (wheel
    /// or pinch, focused on the cursor/pinch midpoint), then clamp so the visible world stays inside
    /// <paramref name="worldBounds"/> (auto-centering when the world is smaller than the view). Call
    /// once per frame after <c>InputManager.Update</c>.
    /// </summary>
    /// <param name="viewport">The render viewport, in virtual screen coordinates. Its
    /// <see cref="Viewport.Bounds"/> is the input region (drags/scrolls/pinches must start inside it).</param>
    /// <param name="worldBounds">The world rectangle the view must stay within.</param>
    public void Update(Viewport viewport, Rectangle worldBounds)
    {
        _lastViewport = viewport;
        Rectangle bounds = viewport.Bounds;
        if (BlockInput) _input.BlockInputRegion(bounds);

        if (_input.TryGetPinch(out Pinch pinch))
        {
            _pinch.Apply(_camera, pinch, viewport, EnablePan, EnableZoom, MinZoom, MaxZoom);
        }
        else
        {
            _pinch.Reset();

            if (EnablePan)
                _camera.PanByScreenDelta(_input.GetDragDelta(bounds));

            if (EnableZoom)
            {
                int scroll = _input.GetScrollIn(bounds);
                if (scroll != 0)
                    _camera.ZoomAboutScreenPoint(_camera.Zoom * MathF.Pow(WheelZoomStep, scroll / 120f),
                                                 _input.PointerPosition, viewport, MinZoom, MaxZoom);
            }
        }

        _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewport);
    }

    /// <summary>
    /// Mirrors <c>PannableCanvas.TryGetTap</c>: true on the frame the viewport is tapped (press-origin
    /// and release both inside it — the click-through-safe invariant). Returns the press and release
    /// points in world coordinates so the caller can hit-test both and require the same target. A pan
    /// also satisfies the invariant, but the camera moved between press and release, so its press and
    /// release world points differ and the same-target check rejects it. Maps through the viewport
    /// from the most recent <see cref="Update"/>.
    /// </summary>
    public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld) =>
        CameraGestures.TryGetTap(_input, _camera, _lastViewport, out pressWorld, out releaseWorld);
}
