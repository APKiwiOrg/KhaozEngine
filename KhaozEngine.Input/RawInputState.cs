using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace KhaozEngine.Input;

/// <summary>
/// Immutable per-frame snapshot of raw hardware. Tests construct this directly; production builds
/// it in <see cref="MonoGameRawInput"/>. Carries everything the consuming games read so the
/// <see cref="InputManager"/> can derive the unified pointer, edges, and gestures without touching
/// any MonoGame input statics.
/// </summary>
/// <param name="MousePosition">Mouse position in screen pixels.</param>
/// <param name="MouseLeftDown">Whether the left mouse button is down.</param>
/// <param name="MouseMiddleDown">Whether the middle mouse button is down.</param>
/// <param name="MouseRightDown">Whether the right mouse button is down.</param>
/// <param name="ScrollWheelValue">Absolute scroll-wheel value (deltas are derived frame-to-frame).</param>
/// <param name="Keyboard">Current keyboard state.</param>
/// <param name="GamePads">Connected game pad states, indexed by player.</param>
/// <param name="Touches">Active touch points.</param>
/// <param name="WindowBounds">Client bounds in screen pixels; an empty rect disables the in-window check.</param>
public readonly record struct RawInputState(
    Point MousePosition,
    bool MouseLeftDown,
    bool MouseMiddleDown,
    bool MouseRightDown,
    int ScrollWheelValue,
    KeyboardState Keyboard,
    IReadOnlyList<GamePadState> GamePads,
    IReadOnlyList<TouchPoint> Touches,
    Rectangle WindowBounds);
