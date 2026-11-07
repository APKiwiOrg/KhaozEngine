using System;
using System.Numerics;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>An external diagnostic subdivision recipe over the original posed source, without reweighting its skin.</summary>
internal sealed record CoverageResearchSubdivisionVertex(int? OldVertex, int? OldTriangle, double[]? Barycentric);

internal static class CoverageResearchSubdivision
{
    internal static Vector3 Interpolate(Vector3 a, Vector3 b, Vector3 c, double[] barycentric)
    {
        if (barycentric.Length != 3 || Array.Exists(barycentric, value => !double.IsFinite(value) || value < 0)
            || Math.Abs(barycentric[0] + barycentric[1] + barycentric[2] - 1) > 1e-8)
            throw new ArgumentException("Subdivision needs three nonnegative barycentrics summing to one.", nameof(barycentric));
        return new(
            (float)(a.X * barycentric[0] + b.X * barycentric[1] + c.X * barycentric[2]),
            (float)(a.Y * barycentric[0] + b.Y * barycentric[1] + c.Y * barycentric[2]),
            (float)(a.Z * barycentric[0] + b.Z * barycentric[1] + c.Z * barycentric[2]));
    }
}
