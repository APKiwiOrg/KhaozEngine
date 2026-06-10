using Microsoft.Xna.Framework;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A generic pannable viewport: owns a camera offset and lets a caller pan over world-space
/// content larger than a caller-supplied viewport. Drag and wheel pan, clamps to caller-supplied
/// content bounds plus padding, scissor-clips rendering, and exposes world/screen transforms plus
/// a click-through-safe tap helper. No game-specific concepts. Zoom is not implemented (a private
/// <c>_zoom = 1f</c> seam is kept so it can be added later).
///
/// Per frame: set <see cref="Viewport"/> and <see cref="ContentBounds"/>, call Update
/// to pan/clamp, then Draw with a world-space draw callback. Query
/// TryGetTap for the world point(s) tapped this frame.
/// </summary>
public sealed class PannableCanvas
{
    private readonly InputManager _input;
    private Vector2 _cameraOffset;
    private float _zoom = 1f;                  // seam for future zoom; fixed at 1 for now

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

    /// <summary>The current camera offset (pan state). Read-only; change it via panning or the focus helpers.</summary>
    public Vector2 CameraOffset => _cameraOffset;

    private Vector2 ViewportCenter =>
        new(Viewport.X + Viewport.Width / 2f, Viewport.Y + Viewport.Height / 2f);

    /// <summary>Maps a world point to virtual screen coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 world)
    {
        Vector2 c = ViewportCenter;
        return new Vector2(c.X + world.X * _zoom + _cameraOffset.X,
                           c.Y + world.Y * _zoom + _cameraOffset.Y);
    }

    /// <summary>Maps a virtual screen point back to world coordinates (exact inverse of <see cref="WorldToScreen"/>).</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
    {
        Vector2 c = ViewportCenter;
        return new Vector2((screen.X - c.X - _cameraOffset.X) / _zoom,
                           (screen.Y - c.Y - _cameraOffset.Y) / _zoom);
    }

    /// <summary>Centers the camera so <paramref name="world"/> sits at the viewport center, then clamps.</summary>
    public void CenterOn(Vector2 world)
    {
        _cameraOffset = new Vector2(-world.X * _zoom, -world.Y * _zoom);
        Clamp();
    }

    private void Clamp()
    {
        // Placeholder until Task 2; no-op keeps CenterOn usable for the round-trip test.
    }
}
