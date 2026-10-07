using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Outward encoding of a supplied Hit fraction bracket. Geometry and complete candidate
/// coverage are caller premises. Refusal never changes a Hit into Clear or publishes a prefix.</summary>
internal static class CapsuleSweepDistanceEncoding
{
    internal static CapsuleSweepResult Hit(Vector3 displacement, double lowerFraction,
        double upperFraction, float maximumErrorMetres)
    {
        if (!double.IsFinite(lowerFraction) || !double.IsFinite(upperFraction) ||
            lowerFraction < 0 || upperFraction < lowerFraction || upperFraction > 1 ||
            !float.IsFinite(maximumErrorMetres) || maximumErrorMetres < 0 ||
            !float.IsFinite(displacement.X) || !float.IsFinite(displacement.Y) ||
            !float.IsFinite(displacement.Z)) return default;

        Span<double> components = stackalloc double[] { displacement.X, displacement.Y, displacement.Z };
        Span<double> zeros = stackalloc double[] { 0, 0, 0 };
        double proposed = Math.Sqrt(components[0] * components[0] +
            components[1] * components[1] + components[2] * components[2]);
        GeometryInterval length = BoundedGeometryArithmetic.CompareSquaredDistances(
            components, zeros, [proposed], [0d]) == GeometrySign.Zero
            ? GeometryInterval.Exact(proposed)
            : GeometryInterval.Exact(components[0]).Square().Add(GeometryInterval.Exact(components[1]).Square())
                .Add(GeometryInterval.Exact(components[2]).Square()).Sqrt();
        if (!length.IsResolved) return default;
        GeometryInterval from = Distance(length, lowerFraction);
        GeometryInterval to = Distance(length, upperFraction);
        if (!from.IsResolved || !to.IsResolved) return default;
        float lower = Down(Math.Max(0, from.Lower));
        float upper = Up(to.Upper);
        if (!float.IsFinite(lower) || !float.IsFinite(upper) || lower < 0 || upper < lower) return default;
        // A rounded square root cannot establish whether the encoded upper distance overruns the
        // actual request. Refuse an unencodable endpoint instead of clamping away its witness.
        GeometrySign extent = BoundedGeometryArithmetic.CompareSquaredDistances(components, zeros, [upper], [0d]);
        if (extent is not (GeometrySign.Positive or GeometrySign.Zero)) return default;

        GeometryInterval width = lower == upper ? GeometryInterval.Exact(0)
            : GeometryInterval.Exact(upper).Subtract(GeometryInterval.Exact(lower));
        if (!width.IsResolved) return default;
        float error = Up(width.Upper);
        if (!float.IsFinite(error) || error < 0 || error > maximumErrorMetres) return default;
        return new(CapsuleSweepStatus.Hit, lower, upper, error);
    }

    static GeometryInterval Distance(GeometryInterval length, double fraction)
    {
        if (fraction == 0) return GeometryInterval.Exact(0);
        if (length.Lower == length.Upper)
        {
            double proposed = length.Lower * fraction;
            if (BoundedGeometryArithmetic.CompareProducts(length.Lower, fraction, proposed, 1d) == GeometrySign.Zero)
                return GeometryInterval.Exact(proposed);
        }
        return length.Multiply(GeometryInterval.Exact(fraction));
    }

    static float Down(double value)
    {
        float encoded = (float)value;
        return float.IsFinite(encoded) && encoded > value ? MathF.BitDecrement(encoded) : encoded;
    }

    static float Up(double value)
    {
        float encoded = (float)value;
        return float.IsFinite(encoded) && encoded < value ? MathF.BitIncrement(encoded) : encoded;
    }
}
