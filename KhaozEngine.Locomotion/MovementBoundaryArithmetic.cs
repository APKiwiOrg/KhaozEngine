using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// Exact dyadic signs for analytic play-area membership. This concerns centre limits only and does
// not certify or alter any physics-backend sweep. No tolerance can turn an outside point into inside.
internal static class MovementBoundaryArithmetic
{
    readonly record struct Term(ulong Mantissa, int Exponent, int Sign);

    internal static int Compare(float start, float delta, double plane)
    {
        Span<Term> terms = stackalloc Term[3] { Split(start), Split(delta), Split(plane) };
        int exponent = Common(terms);
        return (Expand(terms[0], exponent) + Expand(terms[1], exponent) - Expand(terms[2], exponent)).Sign;
    }

    internal static bool InCircle(float x, float z, float dx, float dz, double cx, double cz, double radius)
    {
        Span<Term> terms = stackalloc Term[7]
        {
            Split(x), Split(dx), Split(cx), Split(z), Split(dz), Split(cz), Split(radius)
        };
        int exponent = Common(terms);
        BigInteger px = Expand(terms[0], exponent) + Expand(terms[1], exponent) - Expand(terms[2], exponent);
        BigInteger pz = Expand(terms[3], exponent) + Expand(terms[4], exponent) - Expand(terms[5], exponent);
        BigInteger r = Expand(terms[6], exponent);
        return px * px + pz * pz <= r * r;
    }

    static Term Split(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        ulong magnitude = (ulong)bits & 0x000f_ffff_ffff_ffffUL;
        int exponent = (int)(((ulong)bits >> 52) & 0x7ff);
        if (exponent != 0) magnitude |= 1UL << 52;
        if (magnitude == 0) return new(0, 0, 0);
        int zeros = BitOperations.TrailingZeroCount(magnitude);
        return new(magnitude >> zeros, (exponent == 0 ? -1074 : exponent - 1075) + zeros, bits < 0 ? -1 : 1);
    }
    static int Common(ReadOnlySpan<Term> values)
    {
        int result = 0;
        bool found = false;
        foreach (Term value in values)
            if (value.Sign != 0 && (!found || value.Exponent < result)) { result = value.Exponent; found = true; }
        return result;
    }
    static BigInteger Expand(Term value, int exponent) => value.Sign == 0 ? BigInteger.Zero
        : (new BigInteger(value.Mantissa) * value.Sign) << (value.Exponent - exponent);
}
