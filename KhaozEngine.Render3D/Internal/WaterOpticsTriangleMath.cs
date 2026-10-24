using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Packed-payload sampling arithmetic. No source arrays or construction geometry are consulted.</summary>
internal static class WaterOpticsTriangleMath
{
    /// <summary>
    /// Exact orientation of binary32 inputs. Each of the six products is exact in binary64, including
    /// binary32 subnormals and extremes. Error-free TwoSum builds a nonoverlapping expansion whose last
    /// nonzero term has the exact sign. No tolerance expands a triangle and no BigInteger allocates here.
    /// A shader must supply an equally exact fallback, not replace this predicate with float cross products.
    /// One bounded portable fallback decodes binary32 into integers in units of 2^-149, then accumulates
    /// these same six signed products in 18 uint32 limbs. Each integer is below 2^277 and the six-product
    /// sum is below 2^557, so 576 signed-magnitude bits suffice without any position welding.
    /// </summary>
    internal static int Orient(float ax, float az, float bx, float bz, float px, float pz)
    {
        Span<double> terms = stackalloc double[6]
        {
            (double)bx * pz, -(double)bx * az, -(double)ax * pz,
            -(double)bz * px, (double)bz * ax, (double)az * px
        };
        Span<double> expansion = stackalloc double[6];
        int length = 0;
        foreach (double term in terms)
        {
            double q = term;
            int nextLength = 0;
            for (int i = 0; i < length; i++)
            {
                double component = expansion[i];
                double sum = q + component;
                double bv = sum - q;
                double av = sum - bv;
                double error = (q - av) + (component - bv);
                if (error != 0) expansion[nextLength++] = error;
                q = sum;
            }
            if (q != 0) expansion[nextLength++] = q;
            length = nextLength;
        }
        return length == 0 ? 0 : Math.Sign(expansion[length - 1]);
    }

    internal static bool Contains(ReadOnlySpan<float> atlas, int triangleAddress, int edgeBase, float x, float z)
    {
        for (int i = 0; i < 3; i++)
        {
            int reference = (int)atlas[triangleAddress + 9 + i];
            int edge = edgeBase + 4 * (Math.Abs(reference) - 1);
            int sign = Orient(atlas[edge], atlas[edge + 1], atlas[edge + 2], atlas[edge + 3], x, z);
            if (reference > 0 ? sign < 0 : sign > 0) return false;
        }
        return true;
    }

    // Explicit binary32 operations, round to nearest. Shader ports must preserve these intermediate
    // roundings and parentheses (precise, no contraction). The constructor bounds their complete error.
    internal static float Interpolate(ReadOnlySpan<float> triangle, float x, float z)
    {
        float qa = triangle[2], qb = triangle[5], qc = triangle[8];
        if (qa == qb && qa == qc) return qa;
        float dx = triangle[3] - triangle[0];
        float dz = triangle[4] - triangle[1];
        float ex = triangle[6] - triangle[0];
        float ez = triangle[7] - triangle[1];
        float px = x - triangle[0];
        float pz = z - triangle[1];
        float detLeft = dx * ez;
        float detRight = dz * ex;
        float determinant = detLeft - detRight;
        float bLeft = px * ez;
        float bRight = pz * ex;
        float cLeft = dx * pz;
        float cRight = dz * px;
        float bNumerator = bLeft - bRight;
        float cNumerator = cLeft - cRight;
        float wb = bNumerator / determinant;
        float wc = cNumerator / determinant;
        float db = qb - qa;
        float dc = qc - qa;
        float bTerm = wb * db;
        float cTerm = wc * dc;
        float near = qa + bTerm;
        return near + cTerm;
    }

    internal static bool Intersects(in WaterOpticsTriangle triangle, Vector2 minimum, Vector2 maximum)
    {
        Vector3 a = triangle.A, b = triangle.B, c = triangle.C;
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < minimum.X || MathF.Min(a.X, MathF.Min(b.X, c.X)) > maximum.X
            || MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < minimum.Y || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > maximum.Y)
            return false;
        int inward = Orient(a.X, a.Z, b.X, b.Z, c.X, c.Z);
        return !Separated(a, b, inward, minimum, maximum) && !Separated(b, c, inward, minimum, maximum)
            && !Separated(c, a, inward, minimum, maximum);
    }

    private static bool Separated(Vector3 a, Vector3 b, int inward, Vector2 lo, Vector2 hi) =>
        Orient(a.X, a.Z, b.X, b.Z, lo.X, lo.Y) * inward < 0
        && Orient(a.X, a.Z, b.X, b.Z, hi.X, lo.Y) * inward < 0
        && Orient(a.X, a.Z, b.X, b.Z, hi.X, hi.Y) * inward < 0
        && Orient(a.X, a.Z, b.X, b.Z, lo.X, hi.Y) * inward < 0;

    // Bin selection is floor(u * count), with 1 selecting the final bin. The selected local coordinate
    // is fl(u * size). Outward bounds cover both products' rounding, including their closed boundaries.
    // Only the acceleration rectangles expand. Source triangle containment is always exact.
    internal static float BinMinimum(float size, int bin, int count)
    {
        if (bin == 0) return 0;
        double bound = (double)size * bin / count - size * (4.0 / 16777216);
        return MathF.Max(0, float.BitDecrement((float)Math.Max(0, bound)));
    }

    internal static float BinMaximum(float size, int bin, int count)
    {
        if (bin == count - 1) return size;
        double bound = (double)size * (bin + 1) / count + size * (4.0 / 16777216);
        return MathF.Min(size, float.BitIncrement((float)Math.Min(size, bound)));
    }
}
