using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

/// <summary>
/// Maps raw screen-pixel positions into the game's virtual/world coordinate space. The
/// <see cref="InputManager"/> routes every pointer position through this seam so a future
/// resolution or camera change does not churn callers.
/// </summary>
public interface ICoordinateTransform
{
    /// <summary>Converts a screen-pixel position to virtual coordinates.</summary>
    Vector2 ScreenToVirtual(Vector2 screen);

    /// <summary>
    /// When set, the <see cref="InputManager"/> clamps the transformed pointer into these bounds
    /// (the mouse can report positions outside the client area). Null disables clamping.
    /// </summary>
    Rectangle? VirtualBounds { get; }
}
