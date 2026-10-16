using System;
using System.Numerics;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>Outward position arithmetic for affine model followed by arbitrary finite projective pass. Float
/// operations may reassociate or fuse. Gradual underflow and input/output denormal flushing are both bounded.
/// No camera decomposition, CPU-folded MVP or depth/raster accuracy is assumed.</summary>
internal static class CoverageRenderArithmetic
{
    // One full binary32 ulp also covers directed rounding. Nearest rounding is a tighter subset.
    const double UnitRoundoff = 1.0 / (1 << 23), MinNormal = 1.1754943508222875e-38;

    internal static bool TryBound(in CoverageRenderPass pass, in Matrix4x4 absolute, Vector3 origin, out double error)
    {
        error = 0;
        Span<CoverageScalarBounds> model = stackalloc CoverageScalarBounds[16], vp = stackalloc CoverageScalarBounds[16];
        Load(pass.Model, model); Load(pass.ViewProjection, vp);
        Span<CoverageScalarBounds> inverse = stackalloc CoverageScalarBounds[16];
        if (!TryInverse(vp, inverse)) return false;
        Span<CoverageScalarBounds> local = stackalloc CoverageScalarBounds[4]
        {
            new(pass.LocalMin.X, pass.LocalMax.X), new(pass.LocalMin.Y, pass.LocalMax.Y),
            new(pass.LocalMin.Z, pass.LocalMax.Z), One,
        };
        Span<double> localMagnitude = stackalloc double[4], worldMagnitude = stackalloc double[4], modelError = stackalloc double[3];
        for (int i = 0; i < 4; i++) localMagnitude[i] = Abs(local[i]);
        for (int j = 0; j < 3; j++)
        {
            CoverageScalarBounds world = Zero;
            for (int i = 0; i < 4; i++) world = Add(world, Multiply(local[i], model[i * 4 + j]));
            if (!world.IsFinite || !DotError(localMagnitude, model, j, out modelError[j])) return false;
            worldMagnitude[j] = Sum(Abs(world), Sum(modelError[j], pass.PositionPerturbationBound));
            if (!double.IsFinite(worldMagnitude[j]) || worldMagnitude[j] > float.MaxValue) return false;
        }
        // Affine model column four is exactly (0,0,0,1), so the shader produces exactly world.w = 1.
        worldMagnitude[3] = 1;
        Span<double> clipError = stackalloc double[4], pulled = stackalloc double[4];
        for (int j = 0; j < 4; j++)
            if (!DotError(worldMagnitude, vp, j, out clipError[j])) return false;
        for (int j = 0; j < 4; j++)
        {
            double bound = 0;
            for (int i = 0; i < 4; i++) bound = Sum(bound, Product(clipError[i], Abs(inverse[i * 4 + j])));
            pulled[j] = bound;
        }
        // clip + e maps through the exact inverse to (world.xyz + eta.xyz, 1 + eta.w).
        // Its dehomogenized displacement is (eta.xyz - world.xyz * eta.w) / (1 + eta.w).
        // A positive denominator also preserves the triangle's convex projective sheet.
        double denominator = Subtract(One, Point(pulled[3])).Min;
        if (!(denominator > 0)) return false;
        Span<float> translation = stackalloc float[3] { absolute.M41, absolute.M42, absolute.M43 };
        Span<float> origins = stackalloc float[3] { origin.X, origin.Y, origin.Z };
        CoverageScalarBounds squared = Zero;
        for (int j = 0; j < 3; j++)
        {
            double projection = Divide(Point(Sum(pulled[j], Product(worldMagnitude[j], pulled[3]))), Point(denominator)).Max;
            // Canonical float equality does not make the CPU origin subtraction exact.
            double residual = Abs(Subtract(model[12 + j], Subtract(Point(translation[j]), Point(origins[j]))));
            double coordinate = Sum(Sum(modelError[j], pass.PositionPerturbationBound), Sum(projection, residual));
            squared = Add(squared, Square(Point(coordinate)));
        }
        error = Sqrt(squared).Max;
        return double.IsFinite(error);
    }

    // Four products and three additions: gamma_7 * sum |a_i b_i|. Fused schedules only reduce the operation count.
    // DAZ contributes |a|*delta_b + |b|*delta_a at the inputs. Each of the seven operations can additionally flush
    // a result smaller than MinNormal. The 1/(1-7u) factor covers propagation of those absolute errors.
    static bool DotError(ReadOnlySpan<double> magnitude, ReadOnlySpan<CoverageScalarBounds> matrix, int column, out double error)
    {
        double sum = 0, denormal = 0;
        for (int i = 0; i < 4; i++)
        {
            double coefficient = Abs(matrix[i * 4 + column]), value = magnitude[i];
            sum = Sum(sum, Product(value, coefficient));
            double coefficientFlush = coefficient < MinNormal ? coefficient : 0;
            denormal = Sum(denormal, Sum(Product(value, coefficientFlush), Product(coefficient, Math.Min(value, MinNormal))));
        }
        var denominator = Subtract(One, Point(7 * UnitRoundoff));
        error = Divide(Add(Multiply(Point(7 * UnitRoundoff), Point(sum)), Add(Point(denormal), Point(7 * MinNormal))), denominator).Max;
        // This stronger absolute-sum check excludes overflow under every allowed parenthesization.
        return double.IsFinite(error) && Sum(sum, error) <= float.MaxValue;
    }

    // Exact float coefficients enter as singleton intervals. Cofactors and determinant are outward double bounds.
    // Excluding zero from det qualifies every inverse entry, including cancellation and inversion uncertainty.
    // A badly conditioned inverse can refuse here or at the displacement/denominator/margin gates.
    static bool TryInverse(ReadOnlySpan<CoverageScalarBounds> matrix, Span<CoverageScalarBounds> inverse)
    {
        foreach (var value in matrix) if (!value.IsFinite) return false;
        CoverageScalarBounds determinant = Zero;
        for (int column = 0; column < 4; column++)
        {
            var term = Multiply(matrix[column], Cofactor(matrix, 0, column));
            determinant = Add(determinant, term);
        }
        if (!determinant.IsFinite || determinant.Min <= 0 && determinant.Max >= 0) return false;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                var value = Divide(Cofactor(matrix, row, column), determinant);
                if (!value.IsFinite) return false;
                inverse[column * 4 + row] = value;
            }
        return true;
    }

    static CoverageScalarBounds Cofactor(ReadOnlySpan<CoverageScalarBounds> matrix, int row, int column)
    {
        Span<CoverageScalarBounds> m = stackalloc CoverageScalarBounds[9];
        int k = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                if (i != row && j != column) m[k++] = matrix[i * 4 + j];
        var a = Multiply(m[0], Subtract(Multiply(m[4], m[8]), Multiply(m[5], m[7])));
        var b = Multiply(m[1], Subtract(Multiply(m[3], m[8]), Multiply(m[5], m[6])));
        var c = Multiply(m[2], Subtract(Multiply(m[3], m[7]), Multiply(m[4], m[6])));
        var value = Add(Subtract(a, b), c);
        return (row + column) % 2 == 0 ? value : Negate(value);
    }

    static void Load(in Matrix4x4 m, Span<CoverageScalarBounds> values)
    {
        ReadOnlySpan<float> source = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (int i = 0; i < 16; i++) values[i] = Point(source[i]);
    }

    static CoverageScalarBounds Point(double value) => new(value, value);
    static double Abs(CoverageScalarBounds value) => Math.Max(Math.Abs(value.Min), Math.Abs(value.Max));
    internal static double Sum(double a, double b) => Add(Point(a), Point(b)).Max;
    static double Product(double a, double b) => Multiply(Point(a), Point(b)).Max;
}
