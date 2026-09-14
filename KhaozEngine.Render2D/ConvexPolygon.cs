using System;
using System.Numerics;

namespace KhaozEngine.Render2D;

/// <summary>
/// Pure, allocation-free geometry for a convex polygon given as a vertex span in either winding: its signed area, a
/// mitred offset ring, and how far it can be inset before that ring inverts. No GPU, so all of it is headless
/// testable. The drawing half is <see cref="PrimitiveRenderer.FillConvexPolygon"/> and
/// <see cref="PrimitiveRenderer.StrokeConvexPolygon"/>. Results for a concave or self-intersecting polygon are
/// unspecified.
/// </summary>
public static class ConvexPolygon
{
    // 1 + dot(normalIn, normalOut) at or below this is a full reversal of direction, which has no usable bisector.
    // Only a degenerate spike (collinear points doubling back) reaches it.
    const float ReversalEpsilon = 1e-6f;

    /// <summary>
    /// The polygon's signed area by the shoelace formula, closing the last vertex back to the first. Positive when
    /// the vertices run clockwise as drawn in the batch's y-down design space (the same order is counter-clockwise
    /// on y-up maths axes), negative for the other winding, and 0 for fewer than 3 points. Summed relative to the
    /// first vertex, so large screen coordinates do not cost precision.
    /// </summary>
    public static float SignedArea(ReadOnlySpan<Vector2> points)
    {
        int n = points.Length;
        if (n < 3) return 0f;
        Vector2 origin = points[0];
        Vector2 previous = points[1] - origin;
        float twiceArea = 0f;
        for (int i = 2; i < n; i++)
        {
            Vector2 current = points[i] - origin;
            twiceArea += Cross(previous, current);
            previous = current;
        }
        return twiceArea * 0.5f;
    }

    /// <summary>
    /// Writes into <paramref name="destination"/> each vertex of <paramref name="points"/> moved along its mitred
    /// bisector, so that every edge shifts by <paramref name="distance"/> along its own normal: positive grows the
    /// polygon outward and negative shrinks it inward, for either winding (read from <see cref="SignedArea"/>).
    /// Each vertex's miter length is clamped to <c>miterLimit * |distance|</c> along the same bisector, so a sharp
    /// corner cannot spike, and the two edges beside a clamped vertex move a little less than
    /// <paramref name="distance"/> there. A <paramref name="miterLimit"/> below 1, or NaN, is treated as 1.
    /// <para>
    /// The displacement is linear in <paramref name="distance"/>, clamp included, so every ring offset from the same
    /// polygon with the same limit keeps each vertex on the same bisector ray. Two such rings therefore bound one
    /// quad per edge that meets its neighbours exactly at the corners. A repeated vertex takes its direction from
    /// its nearest distinct neighbours. Fewer than 3 points, zero area, or a zero distance copies the points
    /// through unchanged. An inward distance past <see cref="InsetLimit"/> inverts the ring.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than
    /// <paramref name="points"/>, or overlaps it.</exception>
    public static void Offset(ReadOnlySpan<Vector2> points, float distance, Span<Vector2> destination, float miterLimit = 4f)
    {
        int n = points.Length;
        if (destination.Length < n)
            throw new ArgumentException("The destination must hold at least as many vertices as the polygon.", nameof(destination));
        if (n == 0) return;
        if (points.Overlaps(destination))
            throw new ArgumentException("The destination must not overlap the source vertices.", nameof(destination));

        float area = SignedArea(points);
        if (n < 3 || area == 0f || distance == 0f)
        {
            points.CopyTo(destination);
            return;
        }

        float side = area > 0f ? 1f : -1f;
        float maxLength = (miterLimit >= 1f ? miterLimit : 1f) * MathF.Abs(distance);
        for (int i = 0; i < n; i++)
        {
            Vector2 incoming = EdgeDirection(points, i, -1);
            Vector2 outgoing = EdgeDirection(points, i, 1);
            destination[i] = points[i] + MiterOffset(incoming, outgoing, side, distance, maxLength);
        }
    }

    /// <summary>
    /// How far the polygon can be inset before its offset ring inverts: the smallest distance from the vertex
    /// centroid (the mean of the vertices) to any edge's line. Exact for regular polygons, rectangles and
    /// parallelograms. For other shapes the vertex centroid is not the centre of the largest inscribed circle, so
    /// the value is conservative, and a very short edge between two sharp corners can still fold locally before it
    /// is reached. 0 for fewer than 3 points or zero area.
    /// </summary>
    public static float InsetLimit(ReadOnlySpan<Vector2> points)
    {
        int n = points.Length;
        if (n < 3 || SignedArea(points) == 0f) return 0f;

        Vector2 origin = points[0];
        Vector2 sum = Vector2.Zero;
        for (int i = 1; i < n; i++) sum += points[i] - origin;
        Vector2 centroid = sum / n;

        float nearest = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            Vector2 start = points[i] - origin;
            Vector2 edge = points[i + 1 == n ? 0 : i + 1] - points[i];
            float length = edge.Length();
            if (!(length > 0f)) continue;
            float distance = MathF.Abs(Cross(edge, centroid - start)) / length;
            if (distance < nearest) nearest = distance;
        }
        return float.IsFinite(nearest) ? nearest : 0f;
    }

    // True when the polygon can be drawn: at least 3 finite vertices enclosing a finite, non-zero area.
    internal static bool IsDrawable(ReadOnlySpan<Vector2> points)
    {
        if (points.Length < 3) return false;
        foreach (Vector2 point in points)
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        float area = SignedArea(points);
        return area != 0f && float.IsFinite(area);
    }

    // The outer and inner ring distances (positive is outward) for a stroke of `thickness` placed by `alignment`.
    internal static (float Outer, float Inner) StrokeRingDistances(float thickness, StrokeAlignment alignment) => alignment switch
    {
        StrokeAlignment.Inside => (0f, -thickness),
        StrokeAlignment.Center => (thickness * 0.5f, -thickness * 0.5f),
        StrokeAlignment.Outside => (thickness, 0f),
        _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Unknown stroke alignment."),
    };

    // The unit direction of the edge leaving vertex i (step +1) or arriving at it (step -1), skipping repeated
    // vertices. Zero only when every vertex coincides, which the zero-area guards exclude.
    static Vector2 EdgeDirection(ReadOnlySpan<Vector2> points, int i, int step)
    {
        int n = points.Length;
        Vector2 at = points[i];
        for (int k = 1; k < n; k++)
        {
            Vector2 other = points[((i + step * k) % n + n) % n];
            Vector2 edge = step > 0 ? other - at : at - other;
            float lengthSquared = edge.LengthSquared();
            if (lengthSquared > 0f) return edge / MathF.Sqrt(lengthSquared);
        }
        return Vector2.Zero;
    }

    // One vertex's mitred displacement: along the sum of the two edge normals, sized so both edges move by
    // `distance`, then clamped to `maxLength`. A full reversal has no bisector, so it moves the clamped length
    // straight along the arriving edge instead (outward for a positive distance, the spike's own direction).
    static Vector2 MiterOffset(Vector2 incoming, Vector2 outgoing, float side, float distance, float maxLength)
    {
        Vector2 normalIn = OutwardNormal(incoming, side);
        Vector2 normalOut = OutwardNormal(outgoing, side);
        float denominator = 1f + Vector2.Dot(normalIn, normalOut);
        if (denominator <= ReversalEpsilon)
            return incoming * (distance > 0f ? maxLength : -maxLength);

        Vector2 offset = (normalIn + normalOut) * (distance / denominator);
        float lengthSquared = offset.LengthSquared();
        if (lengthSquared > maxLength * maxLength)
            offset *= maxLength / MathF.Sqrt(lengthSquared);
        return offset;
    }

    // For a positive signed area the right-hand normal of an edge points out of the polygon.
    static Vector2 OutwardNormal(Vector2 direction, float side) => new(direction.Y * side, -direction.X * side);

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
