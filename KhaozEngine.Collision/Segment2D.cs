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

    /// <summary>
    /// Shortest distance between two segments <c>[a1, b1]</c> and <c>[a2, b2]</c> (the clamped closest points on
    /// each, not the infinite lines). The building block <see cref="CapsuleCollision"/> needs for capsule/capsule
    /// overlap. A degenerate segment (<c>a == b</c> on either side) reduces to point/segment, and both degenerate
    /// reduces to point/point - so a zero-length capsule behaves as a circle. Returns 0 for crossing segments.
    /// </summary>
    /// <param name="a1">First segment start.</param>
    /// <param name="b1">First segment end.</param>
    /// <param name="a2">Second segment start.</param>
    /// <param name="b2">Second segment end.</param>
    /// <returns>The distance between the nearest points of the two segments.</returns>
    public static float SegmentToSegmentDistance(Vector2 a1, Vector2 b1, Vector2 a2, Vector2 b2)
    {
        // Closest-point-between-two-segments (Ericson, Real-Time Collision Detection). Explicit component math
        // (no Vector2.Dot/Length) keeps the result bit-stable for lockstep sims, matching DistanceToSegment.
        float d1x = b1.X - a1.X; // direction of segment 1
        float d1y = b1.Y - a1.Y;
        float d2x = b2.X - a2.X; // direction of segment 2
        float d2y = b2.Y - a2.Y;
        float rx = a1.X - a2.X;
        float ry = a1.Y - a2.Y;

        float a = d1x * d1x + d1y * d1y; // squared length of segment 1
        float e = d2x * d2x + d2y * d2y; // squared length of segment 2
        float f = d2x * rx + d2y * ry;

        float s, t;

        if (a <= 0f && e <= 0f)
        {
            // Both segments are points: distance is simply |a1 - a2|.
            return MathF.Sqrt(rx * rx + ry * ry);
        }

        if (a <= 0f)
        {
            // Segment 1 is a point: project it onto segment 2.
            s = 0f;
            t = f / e;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
        }
        else
        {
            float c = d1x * rx + d1y * ry;
            if (e <= 0f)
            {
                // Segment 2 is a point: project it onto segment 1.
                t = 0f;
                s = -c / a;
                s = s < 0f ? 0f : s > 1f ? 1f : s;
            }
            else
            {
                // General non-degenerate case.
                float b = d1x * d2x + d1y * d2y;
                float denom = a * e - b * b; // always >= 0

                // If the segments are not parallel, project line 1 onto line 2 and clamp to segment 1; for
                // parallel segments denom == 0, so pick an arbitrary s (0) and let the t recompute below fix it.
                s = denom != 0f ? (b * f - c * e) / denom : 0f;
                s = s < 0f ? 0f : s > 1f ? 1f : s;

                // Closest point on segment 2 to segment-1's chosen point.
                t = (b * s + f) / e;

                // If t fell outside [0, 1], clamp it and recompute s for that clamped t.
                if (t < 0f)
                {
                    t = 0f;
                    s = -c / a;
                    s = s < 0f ? 0f : s > 1f ? 1f : s;
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = (b - c) / a;
                    s = s < 0f ? 0f : s > 1f ? 1f : s;
                }
            }
        }

        // Distance between the two clamped closest points.
        float c1x = a1.X + d1x * s;
        float c1y = a1.Y + d1y * s;
        float c2x = a2.X + d2x * t;
        float c2y = a2.Y + d2y * t;
        float dx = c1x - c2x;
        float dy = c1y - c2y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
