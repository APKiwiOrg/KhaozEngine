#if COVERAGE_FILTER_STUDY
using System;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu.FilterStudy;

// Outward balls for the unrounded polynomial, independent of the exact reference result.
internal readonly record struct StudyBall(double Value, double Error)
{
    internal bool Sign(out int sign)
    {
        sign = 0;
        if (!double.IsFinite(Value) || !double.IsFinite(Error) || Error < 0) return false;
        sign = Math.Sign(Value);
        return Math.Abs(Value) > Error || Value == 0 && Error == 0;
    }

    internal bool Round(out float rounded)
    {
        rounded = (float)Value;
        if (!float.IsFinite(rounded) || !double.IsFinite(Error) || Error < 0) return false;
        double lower = ((double)MathF.BitDecrement(rounded) + rounded) * .5;
        double upper = ((double)MathF.BitIncrement(rounded) + rounded) * .5;
        if (Error == 0) return (float)Value == rounded;
        return Math.BitDecrement(Value - Error) > lower && Math.BitIncrement(Value + Error) < upper;
    }
}

internal static class StudyFilters
{
    const double U32 = 1.0 / (1 << 24), U64 = 1.0 / (1L << 53);
    internal static readonly string[] Modes = ["fp32", "double-single", "fp64"];
    internal static (float A, float B)[] Edge(Vector3 a, Vector3 b, Vector3 p) =>
        [(b.X, p.Y), (-b.X, a.Y), (-a.X, p.Y), (-b.Y, p.X), (b.Y, a.X), (a.Y, p.X)];
    internal static (float A, float B)[] Dot(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        [(b.X, d.X), (-b.X, c.X), (-a.X, d.X), (a.X, c.X), (b.Y, d.Y), (-b.Y, c.Y), (-a.Y, d.Y), (a.Y, c.Y)];

    internal static StudyBall Evaluate(ReadOnlySpan<(float A, float B)> terms, int mode)
    {
        float s32 = 0, hi = 0, lo = 0;
        double s64 = 0, magnitude = 0, error = 0;
        float magnitude32 = 0;
        bool safe = true;
        foreach (var (a, b) in terms)
        {
            double product = (double)a * b;
            magnitude = Up(magnitude + Math.Abs(product));
            if (mode == 0)
            {
                s32 += a * b;
                float upper = product == 0 ? 0 : Upper32(Math.Abs(product));
                magnitude32 = AddUp32(magnitude32, upper);
            }
            else if (mode == 2) s64 += product;
            else
            {
                // This sufficient range guard keeps the complete split/EFT graph normal and finite.
                safe &= Safe(a) && Safe(b) && (a == 0 || b == 0 || MathF.ILogB(MathF.Abs(a)) + MathF.ILogB(MathF.Abs(b)) is >= -78 and <= 100);
                var (p, q) = TwoProduct(a, b);
                if ((double)p + q != product) safe = false;
                var (s, e) = TwoSum(hi, p);
                var (t, te) = TwoSum(lo, q);
                var (v, ve) = TwoSum(t, e);
                (hi, lo) = TwoSum(s, v);
                error = AddUp32((float)error, AddUp32(MathF.Abs(te), MathF.Abs(ve)));
            }
        }
        if (mode == 1)
        {
            double value = (double)hi + lo;
            // Double TwoSum exposes any error when the two float components are far apart.
            double z = value - hi, tail = (hi - (value - z)) + (lo - z);
            return new(value, safe ? AddUp(error, Math.Abs(tail)) : double.PositiveInfinity);
        }
        double u = mode == 0 ? U32 : U64, k = terms.Length * 2;
        double gamma = Math.BitIncrement(k * u / (1 - k * u));
        double bound = Up(gamma * magnitude);
        if (mode == 0) bound = AddUp32(Upper32((double)Upper32(gamma) * magnitude32), Upper32(k * Math.ScaleB(1, -126) / (1 - k * u)));
        return new(mode == 0 ? s32 : s64, bound);
    }

    internal static bool Scalar(float a, float b, float candidate, bool root, int mode)
    {
        if (!float.IsFinite(candidate) || !float.IsFinite(a) || !root && !float.IsFinite(b)) return false;
        if (root && a < 0 || !root && b == 0) return false;
        uint sign = root ? BitConverter.SingleToUInt32Bits(a) & 0x80000000u
            : (BitConverter.SingleToUInt32Bits(a) ^ BitConverter.SingleToUInt32Bits(b)) & 0x80000000u;
        if ((BitConverter.SingleToUInt32Bits(candidate) & 0x80000000u) != sign) return false;
        if (a == 0) return candidate == 0;
        a = MathF.Abs(a); b = MathF.Abs(b); candidate = MathF.Abs(candidate);
        if (candidate == 0) return false;
        float previous = MathF.BitDecrement(candidate), next = MathF.BitIncrement(candidate);
        float down = (float)(((double)candidate - previous) * .5), up = (float)(((double)next - candidate) * .5);
        if (down == 0 || !float.IsFinite(up)) return false;
        int shift = -MathF.ILogB(root ? candidate : b);
        if (root)
        {
            if (!Scale(ref a, 2 * shift) || !Scale(ref candidate, shift) || !Scale(ref down, shift) || !Scale(ref up, shift)) return false;
        }
        else if (!Scale(ref a, shift) || !Scale(ref b, shift)) return false;
        (float, float)[] lower = root
            ? [(a, 1), (-candidate, candidate), (2 * candidate, down), (-down, down)]
            : [(a, 1), (-b, candidate), (b, down)];
        (float, float)[] upper = root
            ? [(a, 1), (-candidate, candidate), (-2 * candidate, up), (-up, up)]
            : [(a, 1), (-b, candidate), (-b, up)];
        var l = Evaluate(lower, mode); var h = Evaluate(upper, mode);
        bool even = (BitConverter.SingleToUInt32Bits(candidate) & 1) == 0;
        return l.Sign(out int ls) && h.Sign(out int hs) && (ls > 0 || ls == 0 && even) && (hs < 0 || hs == 0 && even);
    }

    static bool Safe(float x) => x == 0 || float.IsFinite(x) && MathF.ILogB(MathF.Abs(x)) is >= -100 and <= 100;
    static (float, float) TwoSum(float a, float b)
    {
        float s = a + b, z = s - a;
        return (s, (a - (s - z)) + (b - z));
    }

    static (float, float) TwoProduct(float a, float b)
    {
        float ca = 4097 * a, cb = 4097 * b;
        float ah = ca - (ca - a), al = a - ah, bh = cb - (cb - b), bl = b - bh;
        float p = a * b, e1 = p - ah * bh, e2 = e1 - al * bh, e3 = e2 - ah * bl;
        return (p, al * bl - e3);
    }

    internal static double Up(double x) => x == 0 ? 0 : Math.BitIncrement(x);
    internal static double AddUp(double x, double y) => y == 0 ? x : x == 0 ? y : Up(x + y);
    static float Upper32(double x)
    {
        if (x == 0) return 0;
        float r = (float)x;
        return r <= MathF.ScaleB(1, -126) ? MathF.ScaleB(1, -125) : MathF.BitIncrement(r);
    }
    static float AddUp32(float a, float b) => b == 0 ? a : a == 0 ? b : Upper32((double)a + b);
    static bool Scale(ref float value, int shift)
    {
        double exact = Math.ScaleB((double)value, shift);
        value = MathF.ScaleB(value, shift);
        return float.IsFinite(value) && exact == value && (value == 0 || MathF.Abs(value) > MathF.ScaleB(1, -126));
    }

    internal static BigInteger Exact(ReadOnlySpan<(float A, float B)> terms)
    {
        BigInteger sum = 0;
        foreach (var (a, b) in terms) sum += Integer(a) * Integer(b);
        return sum;
    }

    static BigInteger Integer(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value), mag = bits & 0x7fffffffu;
        if (mag == 0) return BigInteger.Zero;
        int exp = (int)(mag >> 23);
        BigInteger n = exp == 0 ? mag : (mag & 0x7fffffu) | 0x800000u;
        if (exp > 0) n <<= exp - 1;
        return bits >> 31 == 0 ? n : -n;
    }
}
#endif
