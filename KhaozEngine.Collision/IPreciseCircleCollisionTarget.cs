using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Opt-in per-pixel (or otherwise precise) collision refinement. When a collider also implements this,
/// <see cref="CircleCollision"/> calls it after the broad circle/circle test passes, letting the target
/// reject contacts the bounding circle would otherwise accept.
/// </summary>
public interface IPreciseCircleCollisionTarget
{
    /// <summary>
    /// Returns whether a circle at <paramref name="center"/> with the given <paramref name="radius"/>
    /// precisely intersects this target.
    /// </summary>
    bool IntersectsCircle(Vector2 center, float radius);
}
