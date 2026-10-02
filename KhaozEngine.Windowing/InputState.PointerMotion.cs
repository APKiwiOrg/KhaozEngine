using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing;

public sealed partial class InputState
{
    /// <summary>
    /// Mouse movement in window points, both before and during pointer capture. Native snapshots report zero on
    /// the first cursor sample, a missing-mouse frame and either capture transition. The original constructor
    /// assumes a 1x producer and uses <see cref="MouseDelta"/> unchanged.
    /// </summary>
    public Vector2 MouseDeltaPoints { get; }

    /// <summary>
    /// Framebuffer pixels per window point on each axis. Each nonfinite or nonpositive component means 1.
    /// The original constructor reports <see cref="Vector2.One"/>. Positions remain in framebuffer pixels.
    /// </summary>
    public Vector2 FramebufferScale { get; }

    /// <summary>
    /// Create a snapshot with explicit window-point motion and framebuffer scale. The two required motion
    /// arguments precede the original constructor's arguments, whose order and defaults remain unchanged.
    /// <paramref name="mouseDeltaPoints"/> is independent of the legacy <paramref name="mouseDelta"/>, which
    /// remains in its producer's existing units. Scale normalization does not alter either supplied delta.
    /// </summary>
    public InputState(
        Vector2 mouseDeltaPoints, Vector2 framebufferScale,
        IReadOnlySet<Key> down, IReadOnlySet<Key> pressed, IReadOnlySet<Key> released,
        IReadOnlySet<MouseButton> mouseDown, IReadOnlySet<MouseButton> mousePressed,
        Vector2 mousePosition, Vector2 mouseDelta, float scrollDelta, int width, int height,
        IReadOnlyList<GamepadState>? gamepads = null, IReadOnlyList<TouchPoint>? touches = null,
        bool windowFocused = true, IReadOnlySet<Key>? repeated = null,
        IReadOnlySet<MouseButton>? mouseReleased = null,
        string textInput = "", bool textInputAvailable = false, bool pointerCaptured = false)
        : this(down, pressed, released, mouseDown, mousePressed, mousePosition, mouseDelta, scrollDelta,
            width, height, gamepads, touches, windowFocused, repeated, mouseReleased,
            textInput, textInputAvailable, pointerCaptured)
    {
        MouseDeltaPoints = mouseDeltaPoints;
        FramebufferScale = PointerMotionScale.Normalize(framebufferScale);
    }
}
