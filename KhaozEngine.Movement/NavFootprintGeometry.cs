using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Exact projected capsule and column intersection shared by ground and Drop certificates.</summary>
internal static class NavFootprintGeometry
{
    internal static bool Touches(Vector3 from, Vector3 to, double left, double bottom, double size, double radius)
    {
        double right = left + size, top = bottom + size;
        if (Intersects(from, to, left, right, bottom, top)) return true;
        double distance = Math.Min(PointRectangle(from.X, from.Z, left, right, bottom, top),
            PointRectangle(to.X, to.Z, left, right, bottom, top));
        distance = Math.Min(distance, PointSegment(left, bottom, from, to));
        distance = Math.Min(distance, PointSegment(left, top, from, to));
        distance = Math.Min(distance, PointSegment(right, bottom, from, to));
        distance = Math.Min(distance, PointSegment(right, top, from, to));
        return distance <= radius * radius;
    }

    private static bool Intersects(Vector3 a, Vector3 b, double left, double right, double bottom, double top)
    {
        double enter = 0d, exit = 1d;
        return Clip(a.X, (double)b.X - a.X, left, right, ref enter, ref exit) &&
            Clip(a.Z, (double)b.Z - a.Z, bottom, top, ref enter, ref exit);
    }

    private static bool Clip(double start, double delta, double min, double max, ref double enter, ref double exit)
    {
        if (delta == 0d) return start >= min && start <= max;
        double a = (min - start) / delta, b = (max - start) / delta;
        enter = Math.Max(enter, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return enter <= exit;
    }

    private static double PointRectangle(double x, double z, double left, double right, double bottom, double top)
    {
        double dx = x - Math.Clamp(x, left, right), dz = z - Math.Clamp(z, bottom, top);
        return dx * dx + dz * dz;
    }

    private static double PointSegment(double x, double z, Vector3 a, Vector3 b)
    {
        double dx = (double)b.X - a.X, dz = (double)b.Z - a.Z;
        double length = dx * dx + dz * dz;
        double fraction = length == 0d ? 0d : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / length, 0d, 1d);
        double gapX = x - a.X - fraction * dx, gapZ = z - a.Z - fraction * dz;
        return gapX * gapX + gapZ * gapZ;
    }

}
