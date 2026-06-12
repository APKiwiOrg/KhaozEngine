using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.Graphics;

/// <summary>
/// The shared two-finger pinch state machine: on a continuing pinch it pans a <see cref="Camera2D"/>
/// by the midpoint travel and zooms about the pinch midpoint. Owns the across-frame state
/// (whether a pinch was already in progress, and the previous midpoint) so both
/// <see cref="CameraController"/> and <c>PannableCanvas</c> apply identical pinch behaviour.
/// </summary>
public sealed class PinchGestureTracker
{
    private bool _active;
    private Vector2 _prevMidpoint;

    /// <summary>
    /// Applies one pinch frame to <paramref name="camera"/>: when the pinch was already in progress
    /// and <paramref name="enablePan"/>, pans by <c>pinch.Midpoint - previousMidpoint</c>; when
    /// <paramref name="enableZoom"/> and <c>pinch.Scale &gt; 0</c>, zooms by <c>Zoom * pinch.Scale</c>
    /// about the midpoint, clamped to <c>[<paramref name="minZoom"/>, <paramref name="maxZoom"/>]</c>.
    /// The first frame only records the midpoint (no pan). Call <see cref="Reset"/> on a non-pinch frame.
    /// </summary>
    public void Apply(Camera2D camera, Pinch pinch, Viewport viewport,
                      bool enablePan, bool enableZoom, float minZoom, float maxZoom)
    {
        if (_active && enablePan)
            camera.PanByScreenDelta(pinch.Midpoint - _prevMidpoint);

        if (enableZoom && pinch.Scale > 0f)
            camera.ZoomAboutScreenPoint(camera.Zoom * pinch.Scale, pinch.Midpoint, viewport, minZoom, maxZoom);

        _prevMidpoint = pinch.Midpoint;
        _active = true;
    }

    /// <summary>Clears the in-progress flag so the next <see cref="Apply"/> is treated as a fresh
    /// pinch (no pan on its first frame). Call once per frame when no pinch is present.</summary>
    public void Reset() => _active = false;
}
