using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

// Independent shape definition only. No production support map, feature solver or root isolator.
internal static partial class ShortBodyShellOracle
{
    internal static (double Lower, double Upper) Clearance(float radius, float lower, float ring,
        float upper, float angle, float margin)
    {
        Validate(radius, lower, ring, upper, margin);
        // Every admitted binary32 angle below pi/2 is <= its immediate lower binary32 neighbour.
        if (!float.IsFinite(angle) || angle < 0 || angle > 1.5707963f)
            throw new ArgumentException("Angle must be in the upward plane domain.");
        I cosine = Trig(R.From(angle), false), sine = Trig(R.From(angle), true);
        I apex = cosine * R.From(lower);
        I shoulder = cosine * R.From(ring) - sine * R.From(radius);
        I head = cosine * R.From(upper) - sine * R.From(radius);
        R lo = R.Min(apex.Lower, R.Min(shoulder.Lower, head.Lower)) - R.From(margin);
        R hi = R.Min(apex.Upper, R.Min(shoulder.Upper, head.Upper)) - R.From(margin);
        return Outward(new(lo, hi));
    }

    internal static (double Lower, double Upper) SupportBounds(in ConvexQueryShape shape, Vector3 direction)
    {
        // Read only the supplied value fields. Do not obtain expected answers from production validation.
        if (shape.Kind != ConvexQueryKind.ConeCylinder) throw new ArgumentException("Unknown shape.");
        Validate(shape.Radius, shape.LowerY, shape.RingY, shape.UpperY, shape.Margin);
        R x = R.From(direction.X), y = R.From(direction.Y), z = R.From(direction.Z);
        I radial = Root(x * x + z * z) * R.From(shape.Radius);
        I apex = I.Point(R.From(shape.LowerY) * y);
        I shoulder = I.Point(R.From(shape.RingY) * y) + radial;
        I head = I.Point(R.From(shape.UpperY) * y) + radial;
        I support = new(R.Max(apex.Lower, R.Max(shoulder.Lower, head.Lower)),
            R.Max(apex.Upper, R.Max(shoulder.Upper, head.Upper)));
        return Outward(support + Root(x * x + y * y + z * z) * R.From(shape.Margin));
    }

    static void Validate(float radius, float lower, float ring, float upper, float margin)
    {
        if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(lower) ||
            !float.IsFinite(ring) || !float.IsFinite(upper) || lower >= ring || ring >= upper ||
            (margin != 0 && margin != 0.001f)) throw new ArgumentException("Invalid oracle shape.");
    }

    // Alternating rational Taylor series. After 40 terms the remainder lies between zero
    // and the next positive term for 0 <= x < 2. No libm result enters the certificate.
    static I Trig(R x, bool sine)
    {
        R term = sine ? x : R.One, sum = R.Zero, square = x * x;
        for (int k = 0; k < 40; k++)
        {
            sum += (k & 1) == 0 ? term : -term;
            int a = 2 * k + (sine ? 2 : 1);
            term = term * square / new R(a * (a + 1), 1);
        }
        return new(sum, sum + term);
    }

    // Floor sqrt of the scaled rational, with an exact square test for a singleton interval.
    // Relative precision also covers subnormal binary32 directions and extreme finite dimensions.
    static I Root(R value)
    {
        if (value.N.IsZero) return I.Point(R.Zero);
        int exponent = checked((int)(value.N.GetBitLength() - value.D.GetBitLength()));
        int scale = 192 - (int)Math.Floor(exponent / 2.0);
        BigInteger numerator = value.N << (2 * scale);
        BigInteger integer = numerator / value.D;
        BigInteger root = BigInteger.One << checked((int)((integer.GetBitLength() + 1) / 2));
        while (true)
        {
            BigInteger next = (root + integer / root) >> 1;
            if (next >= root) break;
            root = next;
        }
        BigInteger denominator = BigInteger.One << scale;
        R lower = new(root, denominator);
        return new(lower, root * root * value.D == numerator ? lower : new R(root + 1, denominator));
    }

    static (double Lower, double Upper) Outward(I value) => (Down(value.Lower), -Down(-value.Upper));

    // Monotone search over finite positive binary64 encodings. Each comparison is rational,
    // so conversion cannot round inward or overflow a huge Taylor numerator/denominator.
    static double Down(R value)
    {
        if (value.N.Sign < 0)
        {
            R positive = -value;
            double lower = Down(positive);
            return R.From(lower).CompareTo(positive) == 0 ? -lower : -Math.BitIncrement(lower);
        }
        ulong lo = 0, hi = 0x7fefffffffffffff;
        while (lo < hi)
        {
            ulong middle = lo + (hi - lo + 1) / 2;
            if (R.From(BitConverter.UInt64BitsToDouble(middle)).CompareTo(value) <= 0) lo = middle;
            else hi = middle - 1;
        }
        return BitConverter.UInt64BitsToDouble(lo);
    }

    readonly record struct I(R Lower, R Upper)
    {
        internal static I Point(R value) => new(value, value);
        public static I operator +(I a, I b) => new(a.Lower + b.Lower, a.Upper + b.Upper);
        public static I operator -(I a, I b) => new(a.Lower - b.Upper, a.Upper - b.Lower);
        public static I operator *(I a, R b) => b.N.Sign < 0
            ? new(a.Upper * b, a.Lower * b) : new(a.Lower * b, a.Upper * b);
    }

    readonly record struct R : IComparable<R>
    {
        internal BigInteger N { get; }
        internal BigInteger D { get; }
        internal static R Zero => new(0, 1);
        internal static R One => new(1, 1);
        internal R(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            N = numerator / divisor;
            D = denominator / divisor;
        }
        internal static R From(double value)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Nonfinite oracle operand.");
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            int exponent = (int)((bits >> 52) & 0x7ff);
            BigInteger significand = bits & 0x000fffffffffffff;
            if (exponent != 0) significand += BigInteger.One << 52;
            if ((bits >> 63) != 0) significand = -significand;
            int shift = exponent == 0 ? -1074 : exponent - 1023 - 52;
            return shift >= 0 ? new(significand << shift, 1) : new(significand, BigInteger.One << -shift);
        }
        public int CompareTo(R other) => (N * other.D).CompareTo(other.N * D);
        internal static R Min(R a, R b) => a.CompareTo(b) <= 0 ? a : b;
        internal static R Max(R a, R b) => a.CompareTo(b) >= 0 ? a : b;
        public static R operator +(R a, R b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
        public static R operator -(R a, R b) => a + -b;
        public static R operator -(R a) => new(-a.N, a.D);
        public static R operator *(R a, R b) => new(a.N * b.N, a.D * b.D);
        public static R operator /(R a, R b) => new(a.N * b.D, a.D * b.N);
    }
}
