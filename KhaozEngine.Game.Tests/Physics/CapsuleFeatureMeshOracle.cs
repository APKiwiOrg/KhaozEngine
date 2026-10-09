using System;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Proposed test-only extraction of the existing polyhedron oracle. No production geometry is used.
internal static class CapsuleFeatureMeshOracle
{
    internal static readonly Rational PositionCeiling = new(1, 4000);
    internal static readonly Rational SeparationWidthCeiling = new(1, 10000);
    internal static readonly Rational NormalCeiling = new(1, 100000);

    internal static ExactVector Point(double x, double y, double z) =>
        new(Rational.From(x), Rational.From(y), Rational.From(z));

    internal static ExactVector Point(Vector3 point) => Point(point.X, point.Y, point.Z);

    internal static ExactVector Direction(int x, int y, int z, int denominator = 1) =>
        new(new Rational(x, denominator), new Rational(y, denominator), new Rational(z, denominator));

    internal static Rational AssertError(float value, Rational ceiling)
    {
        Rational exact = Rational.From(value);
        Assert.True(exact >= Rational.Zero && exact <= ceiling,
            "Reported error must be finite, nonnegative and within the exact ceiling.");
        return exact;
    }

    internal static bool VectorWithin(Vector3 actual, ExactVector expected, Rational bound) =>
        (Point(actual) - expected).LengthSquared() <= bound * bound;

    internal static void AssertVectorWithin(Vector3 actual, ExactVector expected, Rational bound, string field) =>
        Assert.True(VectorWithin(actual, expected, bound),
            $"The claimed {field} error must enclose its exact Euclidean vector error.");

    internal static void AssertTangency(ExactVector axis, ExactVector geometry, ExactVector normal, float radius)
    {
        Rational r = Rational.From(radius);
        Assert.Equal(new Rational(1, 1), normal.LengthSquared());
        Assert.Equal(r * r, (axis - geometry).LengthSquared());
        Assert.Equal(normal * r, axis - geometry);
    }

    internal readonly record struct ExactVector(Rational X, Rational Y, Rational Z)
    {
        internal Rational LengthSquared() => X * X + Y * Y + Z * Z;
        public static ExactVector operator +(ExactVector a, ExactVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static ExactVector operator -(ExactVector a, ExactVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static ExactVector operator *(ExactVector a, Rational b) => new(a.X * b, a.Y * b, a.Z * b);
    }

    // IEEE decoding keeps represented floats and doubles exact, including subnormals and signed zero.
    internal readonly record struct Rational
    {
        readonly BigInteger _numerator;
        readonly BigInteger _denominator;
        internal static Rational Zero => new(0, 1);

        internal Rational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new ArgumentOutOfRangeException(nameof(denominator));
            if (denominator.Sign < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }
            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            _numerator = numerator / divisor;
            _denominator = denominator / divisor;
        }

        internal static Rational From(double value)
        {
            Assert.True(double.IsFinite(value), "Numeric witnesses, bounds and errors must be finite.");
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            int encodedExponent = (int)((bits >> 52) & 0x7ff);
            BigInteger numerator = bits & 0x000f_ffff_ffff_ffffUL;
            if (encodedExponent != 0) numerator += BigInteger.One << 52;
            int exponent = encodedExponent == 0 ? -1074 : encodedExponent - 1023 - 52;
            if ((bits >> 63) != 0) numerator = -numerator;
            return exponent >= 0
                ? new Rational(numerator << exponent, BigInteger.One)
                : new Rational(numerator, BigInteger.One << -exponent);
        }

        public static Rational operator +(Rational a, Rational b) =>
            new(a._numerator * b._denominator + b._numerator * a._denominator, a._denominator * b._denominator);
        public static Rational operator -(Rational a, Rational b) =>
            new(a._numerator * b._denominator - b._numerator * a._denominator, a._denominator * b._denominator);
        public static Rational operator *(Rational a, Rational b) =>
            new(a._numerator * b._numerator, a._denominator * b._denominator);
        public static Rational operator /(Rational a, Rational b) =>
            new(a._numerator * b._denominator, a._denominator * b._numerator);
        public static bool operator <=(Rational a, Rational b) =>
            a._numerator * b._denominator <= b._numerator * a._denominator;
        public static bool operator >=(Rational a, Rational b) =>
            a._numerator * b._denominator >= b._numerator * a._denominator;
        public static bool operator <(Rational a, Rational b) =>
            a._numerator * b._denominator < b._numerator * a._denominator;
        public static bool operator >(Rational a, Rational b) =>
            a._numerator * b._denominator > b._numerator * a._denominator;
        public override string ToString() => $"{_numerator}/{_denominator}";
    }
}
