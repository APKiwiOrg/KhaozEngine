using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Exact-weight arithmetic for generated refinement attributes. A finite float times a small integer weight is
/// exact in double and cannot overflow or underflow there, so a weighted sum of three corners rounds only in its two
/// additions and every result is finite. Allocation free.</summary>
internal static class CoverageWeightedAttributes
{
    // Two double additions err by at most 2^-52 of the summed contribution magnitude. Requiring 2^-25 of that magnitude
    // to survive keeps the direction error below 2^-27, under float resolution, at every scale.
    const double SurvivingFraction = 1.0 / (1 << 25);

    /// <summary>Weighted convex values, each rounded once to float. Bounded by the largest corner, so they stay finite.</summary>
    internal static Vector4 Value(Vector4 a, Vector4 b, Vector4 c, in CoverageRefinedVertex v) =>
        new(Value(a.X, b.X, c.X, v), Value(a.Y, b.Y, c.Y, v), Value(a.Z, b.Z, c.Z, v), Value(a.W, b.W, c.W, v));

    internal static Vector2 Value(Vector2 a, Vector2 b, Vector2 c, in CoverageRefinedVertex v) => new(Value(a.X, b.X, c.X, v), Value(a.Y, b.Y, c.Y, v));

    /// <summary>Unit direction of the weighted corner sum. Every weighted contribution being zero is truly absent data and
    /// returns that zero sum. Returns false when nonzero contributions cancel too far to certify any direction.</summary>
    internal static bool TryDirection(Vector3 a, Vector3 b, Vector3 c, in CoverageRefinedVertex v, out Vector3 unit)
    {
        double x = Sum(a.X, b.X, c.X, v), y = Sum(a.Y, b.Y, c.Y, v), z = Sum(a.Z, b.Z, c.Z, v);
        double mx = Magnitude(a.X, b.X, c.X, v), my = Magnitude(a.Y, b.Y, c.Y, v), mz = Magnitude(a.Z, b.Z, c.Z, v);
        double magnitude = mx * mx + my * my + mz * mz, length = x * x + y * y + z * z;
        if (magnitude == 0)
        {
            unit = new((float)x, (float)y, (float)z); return true;
        }
        if (!(length > magnitude * (SurvivingFraction * SurvivingFraction)))
        {
            unit = default; return false;
        }
        double scale = 1 / Math.Sqrt(length);
        unit = new((float)(x * scale), (float)(y * scale), (float)(z * scale)); return true;
    }

    /// <summary>Tangent direction plus the sign of the weighted handedness. Absent directions return Vector4.Zero.
    /// Handedness is the sign of the exact weighted sum of corner W values. A sum of exactly zero, such as an edge
    /// midpoint between opposite handedness corners, deterministically resolves to +1.</summary>
    internal static bool TryTangent(Vector4 a, Vector4 b, Vector4 c, in CoverageRefinedVertex v, out Vector4 tangent)
    {
        tangent = Vector4.Zero;
        if (!TryDirection(new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z), new(c.X, c.Y, c.Z), v, out var direction)) return false;
        if (direction != Vector3.Zero) tangent = new(direction, Sum(a.W, b.W, c.W, v) < 0 ? -1 : 1);
        return true;
    }

    static float Value(float a, float b, float c, in CoverageRefinedVertex v) => (float)(Sum(a, b, c, v) / v.Denominator);
    static double Sum(float a, float b, float c, in CoverageRefinedVertex v) => (double)a * v.A + (double)b * v.B + (double)c * v.C;
    static double Magnitude(float a, float b, float c, in CoverageRefinedVertex v) =>
        Math.Abs((double)a * v.A) + Math.Abs((double)b * v.B) + Math.Abs((double)c * v.C);
}
