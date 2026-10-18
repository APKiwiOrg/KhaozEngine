using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The exact sign and round-to-nearest, ties-to-even binary32 value of a predicate on binary32 inputs.
/// The sign remains exact when the rounded value underflows to zero. Nonfinite inputs produce an invalid value.</summary>
internal readonly record struct CoverageReferenceScalar(int Sign, float Value);

/// <summary>Exact two-dimensional predicates on binary32 inputs, with correctly rounded binary32 values.</summary>
internal static class CoverageReferencePredicates
{
    /// <summary>Returns the exact sign and RN32 value of (b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X),
    /// for binary32 inputs. RN32 rounds once to nearest binary32, with ties to even, including at midpoints.</summary>
    internal static CoverageReferenceScalar Edge(Vector3 a, Vector3 b, Vector3 p)
    {
        if (!Finite(a) || !Finite(b) || !Finite(p)) return new(0, float.NaN);
        Span<double> expansion = stackalloc double[8];
        int length = EdgeExpansion(a, b, p, expansion);
        return Scalar(expansion[..length]);
    }

    static int EdgeExpansion(Vector3 a, Vector3 b, Vector3 p, Span<double> expansion)
    {
        int length = 0;
        // A binary32 product is exact in binary64. Expand before subtracting coordinates so no difference is rounded.
        CoverageGeometry.Grow(expansion, ref length, (double)b.X * p.Y);
        CoverageGeometry.Grow(expansion, ref length, -(double)b.X * a.Y);
        CoverageGeometry.Grow(expansion, ref length, -(double)a.X * p.Y);
        CoverageGeometry.Grow(expansion, ref length, -(double)b.Y * p.X);
        CoverageGeometry.Grow(expansion, ref length, (double)b.Y * a.X);
        CoverageGeometry.Grow(expansion, ref length, (double)a.Y * p.X);
        return length;
    }

    /// <summary>B30's common power-of-two normalization precedes either RN32 conversion, so flushing cannot
    /// turn two subnormal exact sides into a zero divisor. Float constructions retain the pinned order.</summary>
    internal static bool TryScaledSides(Vector3 s, Vector3 t, Vector3 a, Vector3 b, out float x, out float y)
    {
        x = y = float.NaN;
        if (!Finite(s) || !Finite(t) || !Finite(a) || !Finite(b)) return false;
        Span<double> first = stackalloc double[8], second = stackalloc double[8];
        int na = EdgeExpansion(s, t, a, first), nb = EdgeExpansion(s, t, b, second);
        if (na == 0 || nb == 0 || Math.Sign(first[na - 1]) == Math.Sign(second[nb - 1])) return false;
        var ca = first[..na]; var cb = second[..nb];
        int sa = Math.Sign(ca[^1]), sb = Math.Sign(cb[^1]);
        int exponent = Math.Max(Math.ILogB(Math.Abs(ca[^1])), Math.ILogB(Math.Abs(cb[^1])));
        double binade = Math.ScaleB(1, exponent);
        if (CompareMagnitude(ca, sa, binade) < 0 && CompareMagnitude(cb, sb, binade) < 0) exponent--;
        else if (CompareMagnitude(ca, sa, 2 * binade) >= 0 || CompareMagnitude(cb, sb, 2 * binade) >= 0) exponent++;
        for (int i = 0; i < na; i++) ca[i] = Math.ScaleB(ca[i], -exponent);
        for (int i = 0; i < nb; i++) cb[i] = Math.ScaleB(cb[i], -exponent);
        x = Scalar(ca).Value; y = Scalar(cb).Value;
        return true;
    }

    /// <summary>Returns the exact sign and RN32 value of (b.X-a.X)*(d.X-c.X)+(b.Y-a.Y)*(d.Y-c.Y),
    /// for binary32 inputs. RN32 rounds once to nearest binary32, with ties to even, including at midpoints.</summary>
    internal static CoverageReferenceScalar Dot(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return new(0, float.NaN);
        Span<double> expansion = stackalloc double[8];
        int length = 0;
        CoverageGeometry.Grow(expansion, ref length, (double)b.X * d.X);
        CoverageGeometry.Grow(expansion, ref length, -(double)b.X * c.X);
        CoverageGeometry.Grow(expansion, ref length, -(double)a.X * d.X);
        CoverageGeometry.Grow(expansion, ref length, (double)a.X * c.X);
        CoverageGeometry.Grow(expansion, ref length, (double)b.Y * d.Y);
        CoverageGeometry.Grow(expansion, ref length, -(double)b.Y * c.Y);
        CoverageGeometry.Grow(expansion, ref length, -(double)a.Y * d.Y);
        CoverageGeometry.Grow(expansion, ref length, (double)a.Y * c.Y);
        return Scalar(expansion[..length]);
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

    static CoverageReferenceScalar Scalar(ReadOnlySpan<double> expansion)
    {
        if (expansion.IsEmpty) return new(0, 0);
        int sign = Math.Sign(expansion[^1]);
        double estimate = 0;
        foreach (double component in expansion) estimate += component;
        float rounded = (float)Math.Abs(estimate);
        // The expansion estimate is within a binary64 rounding error, but casting it alone can double-round at a
        // binary32 midpoint. Compare the exact expansion with the adjacent float midpoints to choose the final bits.
        if (rounded >= float.MaxValue)
        {
            double overflowMidpoint = (double)float.MaxValue + Math.ScaleB(1, 103);
            rounded = CompareMagnitude(expansion, sign, overflowMidpoint) >= 0 ? float.PositiveInfinity : float.MaxValue;
        }
        else
        {
            bool odd = (BitConverter.SingleToInt32Bits(rounded) & 1) != 0;
            float previous = MathF.BitDecrement(rounded), next = MathF.BitIncrement(rounded);
            int lower = CompareMagnitude(expansion, sign, ((double)previous + rounded) * .5);
            int upper = CompareMagnitude(expansion, sign, ((double)rounded + next) * .5);
            if (lower < 0 || lower == 0 && odd) rounded = previous;
            else if (upper > 0 || upper == 0 && odd) rounded = next;
        }
        return new(sign, sign < 0 ? -rounded : rounded);
    }

    static int CompareMagnitude(ReadOnlySpan<double> expansion, int sign, double value)
    {
        Span<double> difference = stackalloc double[9];
        for (int i = 0; i < expansion.Length; i++) difference[i] = sign * expansion[i];
        int length = expansion.Length;
        CoverageGeometry.Grow(difference, ref length, -value);
        return length == 0 ? 0 : Math.Sign(difference[length - 1]);
    }
}
