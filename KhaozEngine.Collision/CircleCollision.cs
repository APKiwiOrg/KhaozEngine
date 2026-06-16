using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Circle/circle intersection with optional per-pixel precise refinement. The float math here is
/// deterministic and must stay bit-identical for lockstep sims: <c>distanceSquared &lt;= combined^2</c>,
/// touching circles count as intersecting.
/// </summary>
public static class CircleCollision
{
    /// <summary>Broad circle/circle overlap test. Exactly-touching circles count as intersecting.</summary>
    public static bool Intersects(Vector2 positionA, float radiusA, Vector2 positionB, float radiusB)
    {
        float combinedRadius = radiusA + radiusB;
        // Explicit dx*dx + dy*dy (not Vector2.DistanceSquared) so the result is bit-stable and does NOT depend on
        // the Vector2 library's helper implementation - this is hash-gated for lockstep sims.
        float dx = positionA.X - positionB.X;
        float dy = positionA.Y - positionB.Y;
        return dx * dx + dy * dy <= combinedRadius * combinedRadius;
    }

    /// <summary>Broad circle/circle overlap test reading position + radius from two colliders.</summary>
    public static bool Intersects(ICircleCollider a, ICircleCollider b)
    {
        return Intersects(a.Position, a.Radius, b.Position, b.Radius);
    }

    // Circle overlap refined by precise collision when either side opts in via IPreciseCircleCollisionTarget.
    /// <summary>
    /// Full collision test between two colliders: broad circle overlap, then precise refinement on whichever
    /// side(s) implement <see cref="IPreciseCircleCollisionTarget"/>.
    /// </summary>
    public static bool DoCollidersCollide(ICircleCollider source, ICircleCollider target)
    {
        if (!Intersects(source, target))
        {
            return false;
        }

        if (source is IPreciseCircleCollisionTarget preciseSource
            && !preciseSource.IntersectsCircle(target.Position, target.Radius))
        {
            return false;
        }

        if (target is IPreciseCircleCollisionTarget preciseTarget
            && !preciseTarget.IntersectsCircle(source.Position, source.Radius))
        {
            return false;
        }

        return true;
    }

    // Source is a non-precise circle (e.g. a projectile: position + radius); target keeps its precise
    // refinement. Mirrors DoCollidersCollide(source, target) for a non-precise source.
    /// <summary>
    /// Collision test between a bare circle source and a collider target. The target's precise refinement
    /// (if any) is honoured; the source is treated as a plain circle.
    /// </summary>
    public static bool DoCollidersCollide(Vector2 sourcePosition, float sourceRadius, ICircleCollider target)
    {
        if (!Intersects(sourcePosition, sourceRadius, target.Position, target.Radius))
        {
            return false;
        }

        if (target is IPreciseCircleCollisionTarget preciseTarget
            && !preciseTarget.IntersectsCircle(sourcePosition, sourceRadius))
        {
            return false;
        }

        return true;
    }

    // Source keeps its precise refinement; target is a non-precise circle.
    /// <summary>
    /// Collision test between a collider source and a bare circle target. The source's precise refinement
    /// (if any) is honoured; the target is treated as a plain circle.
    /// </summary>
    public static bool DoCollidersCollide(ICircleCollider source, Vector2 targetPosition, float targetRadius)
    {
        if (!Intersects(source.Position, source.Radius, targetPosition, targetRadius))
        {
            return false;
        }

        if (source is IPreciseCircleCollisionTarget preciseSource
            && !preciseSource.IntersectsCircle(targetPosition, targetRadius))
        {
            return false;
        }

        return true;
    }
}
