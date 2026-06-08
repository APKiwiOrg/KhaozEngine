using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

/// <summary>
/// Default transform: screen pixels are virtual coordinates, with no clamping. Used when a game
/// renders at window resolution with no scaling.
/// </summary>
public sealed class IdentityTransform : ICoordinateTransform
{
    /// <summary>Shared instance (the transform is stateless).</summary>
    public static readonly IdentityTransform Instance = new();

    /// <inheritdoc/>
    public Vector2 ScreenToVirtual(Vector2 screen) => screen;

    /// <inheritdoc/>
    public Rectangle? VirtualBounds => null;
}
