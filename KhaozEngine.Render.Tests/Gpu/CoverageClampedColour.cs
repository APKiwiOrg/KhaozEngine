using System;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Joint feasibility of all three channels after the source's max(yCoCgToRgb(value), 0).</summary>
internal static class CoverageClampedColour
{
    internal static bool Contains(Vector3 value, Vector3 lo, Vector3 hi, float tolerance)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) return false;
        var ycc = new Vector3(.25f * value.X + .5f * value.Y + .25f * value.Z, .5f * value.X - .5f * value.Z,
            -.25f * value.X + .5f * value.Y - .25f * value.Z);
        var at = Vector3.Clamp(ycc, lo, hi);
        var rgb = Vector3.Max(new Vector3(at.X + at.Y - at.Z, at.X + at.Z, at.X - at.Y - at.Z), Vector3.Zero);
        if (Vector3.Abs(value - rgb) is var error && Math.Max(error.X, Math.Max(error.Y, error.Z)) <= tolerance) return true;

        // A zero output channel only constrains the pre-clamp channel from above. It is still checked jointly with
        // the other channels and the finite YCoCg box. The bounded polytope is nonempty exactly when it has a vertex.
        Span<Plane> planes = stackalloc Plane[12];
        int count = 0;
        planes[count++] = new(Vector3.UnitX, hi.X); planes[count++] = new(-Vector3.UnitX, -lo.X);
        planes[count++] = new(Vector3.UnitY, hi.Y); planes[count++] = new(-Vector3.UnitY, -lo.Y);
        planes[count++] = new(Vector3.UnitZ, hi.Z); planes[count++] = new(-Vector3.UnitZ, -lo.Z);
        Add(new(1, 1, -1), value.X, tolerance, planes, ref count);
        Add(new(1, 0, 1), value.Y, tolerance, planes, ref count);
        Add(new(1, -1, -1), value.Z, tolerance, planes, ref count);
        for (int a = 0; a < count - 2; a++)
            for (int b = a + 1; b < count - 1; b++)
                for (int c = b + 1; c < count; c++)
                {
                    Vector3 bc = Vector3.Cross(planes[b].Normal, planes[c].Normal);
                    float determinant = Vector3.Dot(planes[a].Normal, bc);
                    if (determinant == 0) continue;
                    Vector3 ca = Vector3.Cross(planes[c].Normal, planes[a].Normal);
                    Vector3 ab = Vector3.Cross(planes[a].Normal, planes[b].Normal);
                    double x = (bc.X * planes[a].Bound + ca.X * planes[b].Bound + ab.X * planes[c].Bound) / determinant;
                    double y = (bc.Y * planes[a].Bound + ca.Y * planes[b].Bound + ab.Y * planes[c].Bound) / determinant;
                    double z = (bc.Z * planes[a].Bound + ca.Z * planes[b].Bound + ab.Z * planes[c].Bound) / determinant;
                    bool inside = true;
                    for (int p = 0; p < count && inside; p++)
                    {
                        Vector3 n = planes[p].Normal;
                        inside = n.X * x + n.Y * y + n.Z * z <= planes[p].Bound;
                    }
                    if (inside) return true;
                }
        return false;
    }

    static void Add(Vector3 n, float value, float tolerance, Span<Plane> planes, ref int count)
    {
        planes[count++] = new(n, (double)value + tolerance);
        if (value > tolerance) planes[count++] = new(-n, -(double)value + tolerance);
    }

    readonly record struct Plane(Vector3 Normal, double Bound);
}
