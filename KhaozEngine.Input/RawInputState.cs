using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace KhaozEngine.Input;

// Immutable per-frame snapshot of raw hardware. Tests construct this directly;
// production builds it in MonoGameRawInput. Carries everything all three games read.
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
