using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

// One active touch in screen space. State mirrors MonoGame's TouchLocationState.
public readonly record struct TouchPoint(Vector2 Position, TouchLocationState State);
