using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

// Default transform: screen pixels ARE virtual coordinates, no clamp.
public sealed class IdentityTransform : ICoordinateTransform
{
    public static readonly IdentityTransform Instance = new();
    public Vector2 ScreenToVirtual(Vector2 screen) => screen;
    public Rectangle? VirtualBounds => null;
}
