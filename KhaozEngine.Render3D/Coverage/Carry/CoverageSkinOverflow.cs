using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold source envelopes and a conservative allocation-free bound over the composed palette.
/// This preflight does not skin vertices or evaluate the selected table's geometry.</summary>
internal sealed class CoverageSkinOverflow
{
    // Deliberately far below binary32 overflow, including squared directions and 12-weight interpolation sums.
    internal const double CoordinateLimit = 1 << 20;
    readonly double x = 1, y = 1, z = 1, maximumAttribute, weightSum = 1;
    readonly bool valid;

    internal CoverageSkinOverflow(CoverageSourceTopology source)
    {
        valid = !source.SkinVertices.IsEmpty;
        foreach (var vertex in source.SkinVertices)
        {
            x = Math.Max(x, Math.Abs((double)vertex.Position.X));
            y = Math.Max(y, Math.Abs((double)vertex.Position.Y));
            z = Math.Max(z, Math.Abs((double)vertex.Position.Z));
            // Positions also bound the matrix coefficients. Include both transformed direction families,
            // so their dot products cannot overflow before normalization produces an uncovered record.
            x = Math.Max(x, Math.Max(Math.Abs((double)vertex.Normal.X), Math.Abs((double)vertex.Tangent.X)));
            y = Math.Max(y, Math.Max(Math.Abs((double)vertex.Normal.Y), Math.Abs((double)vertex.Tangent.Y)));
            z = Math.Max(z, Math.Max(Math.Abs((double)vertex.Normal.Z), Math.Abs((double)vertex.Tangent.Z)));
            double sum = Math.Abs((double)vertex.BoneWeights.X) + Math.Abs((double)vertex.BoneWeights.Y)
                + Math.Abs((double)vertex.BoneWeights.Z) + Math.Abs((double)vertex.BoneWeights.W);
            weightSum = Math.Max(weightSum, sum);
            maximumAttribute = Math.Max(maximumAttribute, Math.Max(
                Math.Max(Math.Abs((double)vertex.Color.X), Math.Abs((double)vertex.Color.Y)),
                Math.Max(Math.Abs((double)vertex.Color.Z), Math.Abs((double)vertex.Color.W))));
            maximumAttribute = Math.Max(maximumAttribute, Math.Max(Math.Abs((double)vertex.Uv.X), Math.Abs((double)vertex.Uv.Y)));
            maximumAttribute = Math.Max(maximumAttribute, Math.Abs((double)vertex.Tangent.W));
        }
        valid &= double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            && double.IsFinite(weightSum) && double.IsFinite(maximumAttribute);
    }

    internal bool Accepts(ReadOnlySpan<Matrix4x4> palette)
    {
        // Retain the raw source envelope for the all-zero-weight identity path too. No normalization
        // assumption is made about four authored weights, including sums slightly above one.
        if (!valid || Math.Max(Math.Max(x, y), z) * 2 > CoordinateLimit
            || weightSum > CoordinateLimit || maximumAttribute > CoordinateLimit) return false;
        foreach (var matrix in palette)
        {
            double bx = x * Math.Abs((double)matrix.M11) + y * Math.Abs((double)matrix.M21)
                + z * Math.Abs((double)matrix.M31) + Math.Abs((double)matrix.M41);
            double by = x * Math.Abs((double)matrix.M12) + y * Math.Abs((double)matrix.M22)
                + z * Math.Abs((double)matrix.M32) + Math.Abs((double)matrix.M42);
            double bz = x * Math.Abs((double)matrix.M13) + y * Math.Abs((double)matrix.M23)
                + z * Math.Abs((double)matrix.M33) + Math.Abs((double)matrix.M43);
            // Twice the absolute ideal bound leaves ample space for the retained independent binary32
            // multiply/add roundings. This may refuse a finite CPU-reference result, which is the safe side.
            double bound = 2 * weightSum * Math.Max(bx, Math.Max(by, bz));
            if (!double.IsFinite(bound) || bound > CoordinateLimit) return false;
        }
        // Frozen position/attribute recipes have nonnegative barycentric coefficients totaling twelve.
        // Exact generated values stay inside the source envelope, and their pre-division sums are at
        // most twelve times it. That, direction squares and rounding remain many orders below float overflow.
        return true;
    }
}
