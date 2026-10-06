using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

// Test-only closed-form geometry. A capsule intersects a box exactly when its centre sphere
// intersects that box expanded along Y by the capsule's cylindrical half-length.
internal readonly record struct AnalyticBox(Vector3 Min, Vector3 Max)
{
    public bool ContainsPoint(Vector3 point) => point.X >= Min.X && point.X < Max.X &&
        point.Y >= Min.Y && point.Y < Max.Y && point.Z >= Min.Z && point.Z < Max.Z;

    public bool ContainsCapsule(Vector3 centre, float radius, float halfHeight) =>
        centre.X - radius >= Min.X && centre.X + radius <= Max.X &&
        centre.Y - halfHeight >= Min.Y && centre.Y + halfHeight <= Max.Y &&
        centre.Z - radius >= Min.Z && centre.Z + radius <= Max.Z;

    public AnalyticBox Clip(AnalyticBox other) => new(Vector3.Max(Min, other.Min), Vector3.Min(Max, other.Max));
    public bool HasVolume => Min.X < Max.X && Min.Y < Max.Y && Min.Z < Max.Z;

    public bool IntersectCapsule(Vector3 centre, Vector3 delta, float radius, float halfHeight,
        out double enter, out double exit)
    {
        double core = halfHeight - radius;
        double[] lo = [Min.X, Min.Y - core, Min.Z];
        double[] hi = [Max.X, Max.Y + core, Max.Z];
        double[] origin = [centre.X, centre.Y, centre.Z];
        double[] movement = [delta.X, delta.Y, delta.Z];
        var cuts = new SortedSet<double> { 0d, 1d };
        for (int axis = 0; axis < 3; axis++)
        {
            if (movement[axis] == 0d) continue;
            AddCut(cuts, (lo[axis] - origin[axis]) / movement[axis]);
            AddCut(cuts, (hi[axis] - origin[axis]) / movement[axis]);
        }
        double[] points = new double[cuts.Count];
        cuts.CopyTo(points);
        enter = double.PositiveInfinity;
        exit = double.NegativeInfinity;
        // Closed endpoints are evaluated directly. A quadratic root can round just beyond its
        // segment even when that endpoint's squared distance equals the squared radius exactly.
        foreach (double at in points)
        {
            double squaredDistance = 0d;
            for (int axis = 0; axis < 3; axis++)
            {
                double position = origin[axis] + movement[axis] * at;
                double distance = position - Math.Clamp(position, lo[axis], hi[axis]);
                squaredDistance += distance * distance;
            }
            if (squaredDistance > (double)radius * radius) continue;
            enter = Math.Min(enter, at);
            exit = Math.Max(exit, at);
        }
        for (int i = 0; i + 1 < points.Length; i++)
        {
            double start = points[i], end = points[i + 1], middle = (start + end) * 0.5d;
            double a = 0d, b = 0d, c = -(double)radius * radius;
            for (int axis = 0; axis < 3; axis++)
            {
                double position = origin[axis] + movement[axis] * middle;
                if (position >= lo[axis] && position <= hi[axis]) continue;
                double q = origin[axis] - (position < lo[axis] ? lo[axis] : hi[axis]);
                double v = movement[axis];
                a += v * v;
                b += 2d * q * v;
                c += q * q;
            }
            if (a == 0d)
            {
                if (c > 0d) continue;
            }
            else
            {
                double discriminant = b * b - 4d * a * c;
                if (discriminant < 0d) continue;
                double root = Math.Sqrt(discriminant);
                start = Math.Max(start, (-b - root) / (2d * a));
                end = Math.Min(end, (-b + root) / (2d * a));
                if (start > end) continue;
            }
            enter = Math.Min(enter, start);
            exit = Math.Max(exit, end);
        }
        return enter <= exit;
    }

    public bool ContainedInterval(Vector3 centre, Vector3 delta, float radius, float halfHeight,
        out double enter, out double exit)
    {
        Vector3 extent = new(radius, halfHeight, radius);
        var inner = new AnalyticBox(Min + extent, Max - extent);
        if (!inner.HasVolume) { enter = exit = 0d; return false; }
        return inner.IntersectCapsule(centre, delta, 0f, 0f, out enter, out exit);
    }

    public Vector3 ContactNormal(Vector3 centre, float radius, float halfHeight)
    {
        Vector3 core = Vector3.UnitY * (halfHeight - radius);
        Vector3 min = Min - core, max = Max + core;
        Vector3 offset = centre - Vector3.Clamp(centre, min, max);
        if (offset.LengthSquared() > 1e-12f) return Vector3.Normalize(offset);
        float[] distances = [centre.X - min.X, max.X - centre.X, centre.Y - min.Y,
            max.Y - centre.Y, centre.Z - min.Z, max.Z - centre.Z];
        Vector3[] normals = [-Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY,
            Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ];
        int nearest = 0;
        for (int i = 1; i < distances.Length; i++) if (distances[i] < distances[nearest]) nearest = i;
        return normals[nearest];
    }

    // Only exact rectangular unions are used as dry-coverage certificates. An L-shaped union
    // is not replaced by its bounding box.
    public bool TryMerge(AnalyticBox other, out AnalyticBox merged)
    {
        bool x = Min.Y == other.Min.Y && Max.Y == other.Max.Y && Min.Z == other.Min.Z && Max.Z == other.Max.Z &&
            Min.X <= other.Max.X && other.Min.X <= Max.X;
        bool y = Min.X == other.Min.X && Max.X == other.Max.X && Min.Z == other.Min.Z && Max.Z == other.Max.Z &&
            Min.Y <= other.Max.Y && other.Min.Y <= Max.Y;
        bool z = Min.X == other.Min.X && Max.X == other.Max.X && Min.Y == other.Min.Y && Max.Y == other.Max.Y &&
            Min.Z <= other.Max.Z && other.Min.Z <= Max.Z;
        merged = new AnalyticBox(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
        return x || y || z;
    }

    static void AddCut(SortedSet<double> cuts, double value)
    {
        if (value > 0d && value < 1d) cuts.Add(value);
    }
}
