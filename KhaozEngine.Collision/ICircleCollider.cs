using Microsoft.Xna.Framework;

namespace KhaozEngine.Collision;

/// <summary>
/// A participant in circle/circle collision: a world-space position and a collision radius.
/// Implement on whatever entity type a game uses; the collision helpers read only these two values.
/// </summary>
public interface ICircleCollider
{
    /// <summary>World-space centre of the collision circle.</summary>
    Vector2 Position { get; }

    /// <summary>Collision radius around <see cref="Position"/>.</summary>
    float Radius { get; }
}
