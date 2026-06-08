using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

/// <summary>
/// One active touch. <paramref name="State"/> mirrors MonoGame's <see cref="TouchLocationState"/>.
/// </summary>
/// <param name="Position">Touch position (screen pixels in <see cref="RawInputState"/>; virtual coords when read from <see cref="InputManager.Touches"/>).</param>
/// <param name="State">Pressed / Moved / Released phase.</param>
/// <param name="Id">Stable per-finger id for tracking a touch across frames (0 when unspecified).</param>
public readonly record struct TouchPoint(Vector2 Position, TouchLocationState State, int Id = 0);
