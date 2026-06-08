using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

/// <summary>
/// One active touch in screen space. <paramref name="State"/> mirrors MonoGame's
/// <see cref="TouchLocationState"/> so the production adapter can pass it straight through.
/// </summary>
/// <param name="Position">Touch position in screen pixels.</param>
/// <param name="State">Pressed / Moved / Released phase.</param>
public readonly record struct TouchPoint(Vector2 Position, TouchLocationState State);
