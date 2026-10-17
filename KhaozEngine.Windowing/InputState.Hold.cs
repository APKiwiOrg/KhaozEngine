using System;
using System.Numerics;

namespace KhaozEngine.Windowing;

public sealed partial class InputState
{
    /// <summary>
    /// The frame as a consumer sees it while something above it holds input: every key and button set, both
    /// deltas, scroll, pointer samples, touches, gamepads and committed text are cleared. Window size, focus,
    /// <see cref="MousePosition"/>, <see cref="FramebufferScale"/>, <see cref="PointerCaptured"/> and
    /// <see cref="TextInputAvailable"/> are kept, so layout and hover-free reads stay correct. A key held across
    /// the hold reappears in <see cref="KeysDown"/> afterwards without a press edge.
    /// </summary>
    public InputState WithoutInteraction() =>
        new(Gestures.IsAvailable ? PointerGestureFrame.Empty : PointerGestureFrame.Unavailable, 0f, Vector2.Zero,
            FramebufferScale, EmptyKeys, EmptyKeys, EmptyKeys, EmptyMouseButtons, EmptyMouseButtons,
            MousePosition, Vector2.Zero, 0f, Width, Height, Array.Empty<GamepadState>(), Array.Empty<TouchPoint>(),
            WindowFocused, EmptyKeys, EmptyMouseButtons, "", TextInputAvailable, PointerCaptured);
}
