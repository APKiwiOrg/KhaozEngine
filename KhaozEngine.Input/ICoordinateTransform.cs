using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

// Maps raw screen-pixel positions into the game's virtual/world coordinate space.
// The InputManager routes every pointer position through this seam so a future
// resolution/camera change does not churn callers.
public interface ICoordinateTransform
{
    Vector2 ScreenToVirtual(Vector2 screen);

    // When set, the InputManager clamps the transformed pointer into these bounds
    // (mouse can report positions outside the client area). Null = no clamp.
    Rectangle? VirtualBounds { get; }
}
