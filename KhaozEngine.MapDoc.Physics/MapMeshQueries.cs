using System;
using KhaozEngine.Physics;
using static KhaozEngine.MapDoc.Physics.MapShapeQueries;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Distance and ray queries against a triangle mesh in scalar double: segment to triangle distance on each
/// triangle clipped to the band, and a two-sided ray to triangle test.</summary>
internal static class MapMeshQueries
{
    internal static double Measure(in MapProbe probe, TriangleMeshShape mesh, MapDoublePose pose, MapSlab band)
    {
        MapDouble3[] vertices = Centred(pose, mesh.Vertices);
        double min = band.Min - pose.Position.Y, max = band.Max - pose.Position.Y;
        MapDouble3 p0 = probe.Bottom.Subtract(pose.Position), p1 = probe.Top.Subtract(pose.Position);
        Span<MapDouble3> polygon = stackalloc MapDouble3[8];
        Span<MapDouble3> scratch = stackalloc MapDouble3[8];
        int[] indices = mesh.Indices;
        double best = double.PositiveInfinity;
        for (int i = 0; i + 2 < indices.Length && best > 0d; i += 3)
        {
            polygon[0] = vertices[indices[i]];
            polygon[1] = vertices[indices[i + 1]];
            polygon[2] = vertices[indices[i + 2]];
            int count = 3;
            if (!double.IsNegativeInfinity(min))
            {
                count = ClipPolygon(polygon, count, min, keepAbove: true, scratch);
                scratch[..count].CopyTo(polygon);
            }
            if (!double.IsPositiveInfinity(max))
            {
                count = ClipPolygon(polygon, count, max, keepAbove: false, scratch);
                scratch[..count].CopyTo(polygon);
            }
            for (int j = 1; j + 1 < count; j++)
                best = Math.Min(best, SegmentToTriangle(p0, p1, polygon[0], polygon[j], polygon[j + 1]));
        }
        return double.IsPositiveInfinity(best) ? best : Surface(best, probe.Radius);
    }

    // One Sutherland-Hodgman pass against the plane at height y. A triangle grows to at most five points.
    static int ClipPolygon(ReadOnlySpan<MapDouble3> input, int count, double y, bool keepAbove, Span<MapDouble3> output)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            MapDouble3 current = input[i], next = input[(i + 1) % count];
            bool currentIn = keepAbove ? current.Y >= y : current.Y <= y;
            bool nextIn = keepAbove ? next.Y >= y : next.Y <= y;
            if (currentIn) output[n++] = current;
            if (currentIn == nextIn) continue;
            double s = (y - current.Y) / (next.Y - current.Y);
            output[n++] = new MapDouble3(current.X + s * (next.X - current.X), y, current.Z + s * (next.Z - current.Z));
        }
        return n;
    }

    static double SegmentToTriangle(MapDouble3 p0, MapDouble3 p1, MapDouble3 a, MapDouble3 b, MapDouble3 c)
    {
        MapDouble3 normal = MapDouble3.Cross(b.Subtract(a), c.Subtract(a));
        if (Dot(normal, normal) > 0d)
        {
            double d0 = Dot(normal, p0.Subtract(a)), d1 = Dot(normal, p1.Subtract(a));
            if (d0 != d1 && ((d0 <= 0d && d1 >= 0d) || (d0 >= 0d && d1 <= 0d)))
            {
                MapDouble3 x = Lerp(p0, p1, d0 / (d0 - d1));
                if (Dot(MapDouble3.Cross(b.Subtract(a), x.Subtract(a)), normal) >= 0d &&
                    Dot(MapDouble3.Cross(c.Subtract(b), x.Subtract(b)), normal) >= 0d &&
                    Dot(MapDouble3.Cross(a.Subtract(c), x.Subtract(c)), normal) >= 0d)
                    return 0d;
            }
        }
        double best = Length(MapConvexQueries.ClosestOnTriangle(p0, a, b, c).Subtract(p0));
        best = Math.Min(best, Length(MapConvexQueries.ClosestOnTriangle(p1, a, b, c).Subtract(p1)));
        best = Math.Min(best, SegmentToSegment(p0, p1, a, b));
        best = Math.Min(best, SegmentToSegment(p0, p1, b, c));
        return Math.Min(best, SegmentToSegment(p0, p1, c, a));
    }

    static double Length(MapDouble3 v) => Math.Sqrt(Dot(v, v));

    // The closest points of two segments, after Ericson, Real-Time Collision Detection 5.1.9.
    static double SegmentToSegment(MapDouble3 p1, MapDouble3 q1, MapDouble3 p2, MapDouble3 q2)
    {
        MapDouble3 d1 = q1.Subtract(p1), d2 = q2.Subtract(p2), r = p1.Subtract(p2);
        double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r), s, t;
        if (a == 0d && e == 0d) return Length(r);
        if (a == 0d)
        {
            s = 0d;
            t = Math.Clamp(f / e, 0d, 1d);
        }
        else
        {
            double c = Dot(d1, r);
            if (e == 0d)
            {
                t = 0d;
                s = Math.Clamp(-c / a, 0d, 1d);
            }
            else
            {
                double b = Dot(d1, d2), denominator = a * e - b * b;
                s = denominator != 0d ? Math.Clamp((b * f - c * e) / denominator, 0d, 1d) : 0d;
                t = (b * s + f) / e;
                if (t < 0d)
                {
                    t = 0d;
                    s = Math.Clamp(-c / a, 0d, 1d);
                }
                else if (t > 1d)
                {
                    t = 1d;
                    s = Math.Clamp((b - c) / a, 0d, 1d);
                }
            }
        }
        return Length(Lerp(p1, q1, s).Subtract(Lerp(p2, q2, t)));
    }

    internal static bool Cast(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, TriangleMeshShape mesh,
        MapDoublePose pose, out double t, out MapDouble3 normal)
    {
        t = 0d;
        normal = default;
        MapDouble3[] vertices = Centred(pose, mesh.Vertices);
        MapDouble3 o = origin.Subtract(pose.Position);
        int[] indices = mesh.Indices;
        bool any = false;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            MapDouble3 a = vertices[indices[i]];
            MapDouble3 e1 = vertices[indices[i + 1]].Subtract(a), e2 = vertices[indices[i + 2]].Subtract(a);
            MapDouble3 p = MapDouble3.Cross(direction, e2);
            double determinant = Dot(e1, p);
            if (determinant == 0d) continue;
            double inverse = 1d / determinant;
            MapDouble3 s = o.Subtract(a);
            double u = Dot(s, p) * inverse;
            if (u < 0d || u > 1d) continue;
            MapDouble3 q = MapDouble3.Cross(s, e1);
            double v = Dot(direction, q) * inverse;
            if (v < 0d || u + v > 1d) continue;
            double hit = Dot(e2, q) * inverse;
            if (hit < tStart || hit > tEnd || (any && hit >= t)) continue;
            any = true;
            t = hit;
            normal = MapDouble3.Cross(e1, e2);
            if (Dot(normal, direction) > 0d) normal = normal.Scale(-1d);
        }
        return any;
    }
}
