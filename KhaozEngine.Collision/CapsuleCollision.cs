using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Capsule (pill) collision: a capsule is the segment <c>[a, b]</c> inflated by a radius. The sibling of
/// <see cref="CircleCollision"/>, built on <see cref="Segment2D"/>. A degenerate capsule (<c>a == b</c>) reduces
/// exactly to a circle. The math is deterministic - it delegates to the explicit component arithmetic in
/// <see cref="Segment2D"/>, so it stays bit-stable for lockstep sims - and, like <see cref="CircleCollision"/>,
/// touching counts as intersecting (<c>&lt;=</c>).
/// </summary>
public static class CapsuleCollision
{
    /// <summary>
    /// Circle vs capsule overlap: true when the circle at <paramref name="circleCenter"/> of radius
    /// <paramref name="circleRadius"/> overlaps the capsule <c>[<paramref name="a"/>, <paramref name="b"/>]</c> of
    /// radius <paramref name="capsuleRadius"/>. Exactly-touching counts as intersecting.
    /// </summary>
    public static bool Intersects(Vector2 a, Vector2 b, float capsuleRadius, Vector2 circleCenter, float circleRadius)
    {
        return Segment2D.DistanceToSegment(circleCenter, a, b, out _) <= capsuleRadius + circleRadius;
    }

    /// <summary>
    /// Point-in-capsule test: true when <paramref name="point"/> lies within the capsule
    /// <c>[<paramref name="a"/>, <paramref name="b"/>]</c> of radius <paramref name="capsuleRadius"/>. A point
    /// exactly on the surface counts as inside.
    /// </summary>
    public static bool Contains(Vector2 a, Vector2 b, float capsuleRadius, Vector2 point)
    {
        return Segment2D.DistanceToSegment(point, a, b, out _) <= capsuleRadius;
    }

    /// <summary>
    /// Capsule vs capsule overlap: true when capsule <c>[<paramref name="a1"/>, <paramref name="b1"/>]</c> of
    /// radius <paramref name="radiusA"/> overlaps capsule <c>[<paramref name="a2"/>, <paramref name="b2"/>]</c> of
    /// radius <paramref name="radiusB"/>. Exactly-touching counts as intersecting.
    /// </summary>
    public static bool Intersects(Vector2 a1, Vector2 b1, float radiusA, Vector2 a2, Vector2 b2, float radiusB)
    {
        return Segment2D.SegmentToSegmentDistance(a1, b1, a2, b2) <= radiusA + radiusB;
    }
}
