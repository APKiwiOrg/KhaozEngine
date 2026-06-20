using System;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Point/segment geometry helpers. The companion to <see cref="CircleCollision"/> (circle/circle) and
/// <see cref="GridRay"/> (grid raycast): the primitive a swept (look-ahead) collision needs so a fast-moving
/// circle cannot tunnel through a thin target between two frames - test the target against the segment the
/// mover sweeps, not just the two endpoints.
/// </summary>
/// <remarks>
/// The math is deterministic and written with explicit component arithmetic (no <c>Vector2</c> dot/length
/// helpers) so it stays bit-stable for lockstep sims, matching <see cref="CircleCollision"/>.
/// </remarks>
public static class Segment2D
{
    /// <summary>
    /// Shortest distance from point <paramref name="p"/> to the segment <c>[a, b]</c> (the clamped closest point,
    /// not the infinite line). <paramref name="t"/> is the parameter of that closest point along <c>a -&gt; b</c>,
    /// clamped to <c>[0, 1]</c> (<c>t ~ 0</c> near <paramref name="a"/>, <c>t ~ 1</c> near <paramref name="b"/>),
    /// so callers can order hits by position along a swept path. A degenerate segment
    /// (<paramref name="a"/> == <paramref name="b"/>) returns <c>|p - a|</c> with <paramref name="t"/> = 0.
    /// </summary>
    /// <param name="p">The query point.</param>
    /// <param name="a">Segment start.</param>
    /// <param name="b">Segment end.</param>
    /// <param name="t">Outputs the clamped projection parameter of the closest point along the segment.</param>
    /// <returns>The distance from <paramref name="p"/> to the nearest point on the segment.</returns>
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        // Explicit component math (not Vector2.Dot/Length) keeps the result bit-stable for lockstep sims.
        float abx = b.X - a.X;
        float aby = b.Y - a.Y;
        float lengthSquared = abx * abx + aby * aby;

        if (lengthSquared <= 0f)
        {
            // Degenerate segment: no direction to project onto, so the closest point is 'a' itself.
            t = 0f;
            float dx0 = p.X - a.X;
            float dy0 = p.Y - a.Y;
            return MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        // Project (p - a) onto (b - a) and clamp the parameter to the segment.
        float apx = p.X - a.X;
        float apy = p.Y - a.Y;
        float projection = (apx * abx + apy * aby) / lengthSquared;
        t = projection < 0f ? 0f : projection > 1f ? 1f : projection;

        // Distance to the clamped closest point.
        float closestX = a.X + t * abx;
        float closestY = a.Y + t * aby;
        float dx = p.X - closestX;
        float dy = p.Y - closestY;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
