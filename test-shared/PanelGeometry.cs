using System;
using System.Numerics;

namespace KhaozEngine.Tests.Attachments;

internal readonly record struct PanelCapsule(Vector3 Start, Vector3 End, float Radius);
internal readonly record struct PanelTriangleContact(float Clearance, bool Crosses, bool Contained);

internal static class PanelGeometry
{
    private const float SurfaceRoundoff = .000001f;

    internal static PanelTriangleContact Contact(PanelCapsule capsule, Vector3 a, Vector3 b, Vector3 c)
    {
        float farthest = MathF.Max(AxisDistance(a), MathF.Max(AxisDistance(b), AxisDistance(c))) - capsule.Radius;

        // Distance to the axis is convex. Its minimum may be in the triangle's interior,
        // while its maximum is attained at a vertex. The signs bracket any crossing.
        float nearest = MathF.Sqrt(SegmentTriangleDistanceSquared(capsule.Start, capsule.End, a, b, c)) - capsule.Radius;
        return new PanelTriangleContact(nearest, nearest < -SurfaceRoundoff && farthest > SurfaceRoundoff,
            farthest < -SurfaceRoundoff);

        float AxisDistance(Vector3 point) =>
            Vector3.Distance(point, ClosestPointOnSegment(point, capsule.Start, capsule.End));
    }

    internal static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 axis = end - start;
        float squared = axis.LengthSquared();
        return squared == 0 ? start : start + Math.Clamp(Vector3.Dot(point - start, axis) / squared, 0, 1) * axis;
    }

    internal static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float normalSquared = normal.LengthSquared();
        if (normalSquared > 0)
        {
            Vector3 projected = point - normal * (Vector3.Dot(point - a, normal) / normalSquared);
            if (InTriangle(projected, a, b, c, normal))
                return projected;
        }

        Vector3 onAB = ClosestPointOnSegment(point, a, b);
        Vector3 onBC = ClosestPointOnSegment(point, b, c);
        Vector3 onCA = ClosestPointOnSegment(point, c, a);
        Vector3 closest = Vector3.DistanceSquared(point, onAB) <= Vector3.DistanceSquared(point, onBC) ? onAB : onBC;
        return Vector3.DistanceSquared(point, closest) <= Vector3.DistanceSquared(point, onCA) ? closest : onCA;
    }

    private static float SegmentTriangleDistanceSquared(Vector3 start, Vector3 end, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        Vector3 axis = end - start;
        float acrossPlane = Vector3.Dot(axis, normal);
        if (acrossPlane != 0)
        {
            float fraction = Vector3.Dot(a - start, normal) / acrossPlane;
            if (fraction is >= 0 and <= 1 && InTriangle(start + fraction * axis, a, b, c, normal))
                return 0;
        }

        // The other minima occur at an endpoint opposite the face or on an edge opposite the axis.
        float fromStart = Vector3.DistanceSquared(start, ClosestPoint(start, a, b, c));
        float fromEnd = Vector3.DistanceSquared(end, ClosestPoint(end, a, b, c));
        float fromAB = SegmentDistanceSquared(start, end, a, b);
        float fromBC = SegmentDistanceSquared(start, end, b, c);
        float fromCA = SegmentDistanceSquared(start, end, c, a);
        return MathF.Min(MathF.Min(fromStart, fromEnd), MathF.Min(fromAB, MathF.Min(fromBC, fromCA)));
    }

    private static bool InTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal) =>
        Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= 0 &&
        Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= 0 &&
        Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= 0;

    private static float SegmentDistanceSquared(Vector3 start, Vector3 end, Vector3 first, Vector3 second)
    {
        Vector3 axis = end - start;
        Vector3 edge = second - first;
        Vector3 transverse = Vector3.Cross(axis, edge);
        float squared = transverse.LengthSquared();
        if (squared > 0)
        {
            Vector3 offset = first - start;
            float onAxis = Vector3.Dot(Vector3.Cross(offset, edge), transverse) / squared;
            float onEdge = Vector3.Dot(Vector3.Cross(offset, axis), transverse) / squared;
            if (onAxis is >= 0 and <= 1 && onEdge is >= 0 and <= 1)
                return Vector3.DistanceSquared(start + axis * onAxis, first + edge * onEdge);
        }

        float fromStart = Vector3.DistanceSquared(start, ClosestPointOnSegment(start, first, second));
        float fromEnd = Vector3.DistanceSquared(end, ClosestPointOnSegment(end, first, second));
        float fromFirst = Vector3.DistanceSquared(first, ClosestPointOnSegment(first, start, end));
        float fromSecond = Vector3.DistanceSquared(second, ClosestPointOnSegment(second, start, end));
        return MathF.Min(MathF.Min(fromStart, fromEnd), MathF.Min(fromFirst, fromSecond));
    }
}
