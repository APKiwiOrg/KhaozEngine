using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One exact quadratic field, A + B sqrt(D). Signs use unsquared signs before
/// a rational squared comparison. Mixed extensions refuse instead of rounding a predicate.</summary>
internal readonly struct ConvexQuadratic
{
    internal ConvexRational A { get; }
    internal ConvexRational B { get; }
    internal ConvexRational D { get; }
    internal bool Rational => B.IsZero;
    internal int Sign
    {
        get
        {
            if (B.IsZero) return A.Sign;
            if (A.IsZero || A.Sign == B.Sign) return B.Sign;
            int comparison = (A * A - B * B * D).Sign;
            return A.Sign > 0 ? comparison : -comparison;
        }
    }

    internal ConvexQuadratic(ConvexRational a) { A = a; B = D = default; }
    ConvexQuadratic(ConvexRational a, ConvexRational b, ConvexRational d)
    { A = a; B = b; D = b.IsZero ? default : d; }
    internal static ConvexQuadratic From(double value) => new(ConvexRational.From(value));

    internal static ConvexQuadratic Root(ConvexRational value)
    {
        if (value.Sign < 0) throw new ConvexExactRefusalException();
        if (value.IsZero) return default;
        BigInteger n = IntegerRoot(value.Numerator), d = IntegerRoot(value.Denominator);
        return n * n == value.Numerator && d * d == value.Denominator
            ? new(new ConvexRational(n, d)) : new(default, ConvexRational.One, value);
    }

    static BigInteger IntegerRoot(BigInteger n)
    {
        if (n.IsZero) return n;
        BigInteger root = BigInteger.One << checked((int)((n.GetBitLength() + 1) / 2));
        while (true)
        {
            BigInteger next = (root + n / root) >> 1;
            if (next >= root) return root;
            root = next;
        }
    }

    static ConvexRational Field(ConvexQuadratic a, ConvexQuadratic b)
    {
        if (a.Rational) return b.D;
        if (b.Rational) return a.D;
        if (a.D.CompareTo(b.D) != 0) throw new ConvexExactRefusalException();
        return a.D;
    }

    public static ConvexQuadratic operator -(ConvexQuadratic a) => new(-a.A, -a.B, a.D);
    public static ConvexQuadratic operator +(ConvexQuadratic a, ConvexQuadratic b) =>
        new(a.A + b.A, a.B + b.B, Field(a, b));
    public static ConvexQuadratic operator -(ConvexQuadratic a, ConvexQuadratic b) => a + -b;
    public static ConvexQuadratic operator *(ConvexQuadratic a, ConvexQuadratic b)
    {
        ConvexRational d = Field(a, b);
        return new(a.A * b.A + a.B * b.B * d, a.A * b.B + a.B * b.A, d);
    }
    public static ConvexQuadratic operator /(ConvexQuadratic a, ConvexQuadratic b)
    {
        ConvexRational d = Field(a, b), norm = b.A * b.A - b.B * b.B * d;
        if (norm.IsZero) throw new ConvexExactRefusalException();
        return new((a.A * b.A - a.B * b.B * d) / norm, (a.B * b.A - a.A * b.B) / norm, d);
    }

    internal GeometryInterval Bounds
    {
        get
        {
            GeometryInterval a = Enclose(A);
            return Rational ? a : a.Add(Enclose(B).Multiply(Enclose(D).Sqrt()));
        }
    }

    internal static GeometryInterval RootBounds(ConvexQuadratic value)
    {
        if (value.Sign < 0) return default;
        if (value.Sign == 0) return GeometryInterval.Exact(0);
        if (value.Rational) return Root(value.A).Bounds;
        var bounds = value.Bounds;
        return bounds.IsResolved ? GeometryInterval.Enclose(Math.Max(0, bounds.Lower), bounds.Upper).Sqrt() : default;
    }

    static GeometryInterval Enclose(ConvexRational r)
    {
        if (r.IsZero) return GeometryInterval.Exact(0);
        double lower = Down(r), upper = -Down(-r);
        return GeometryInterval.Enclose(lower, upper);
    }

    static double Down(ConvexRational r)
    {
        if (r.Sign < 0)
        {
            double positive = Down(-r);
            return ConvexRational.From(positive).CompareTo(-r) == 0 ? -positive : -Math.BitIncrement(positive);
        }
        // The proposal only accelerates conversion. Rational comparisons certify both endpoints.
        double proposal = (double)r.Numerator / (double)r.Denominator;
        if (double.IsFinite(proposal))
            for (int i = 0; i < 4; i++)
            {
                if (ConvexRational.From(proposal).CompareTo(r) > 0) proposal = Math.BitDecrement(proposal);
                else if (double.IsFinite(Math.BitIncrement(proposal)) && ConvexRational.From(Math.BitIncrement(proposal)).CompareTo(r) <= 0)
                    proposal = Math.BitIncrement(proposal);
                else return proposal;
            }
        ulong low = 0, high = 0x7fefffffffffffff;
        while (low < high)
        {
            ulong middle = low + (high - low + 1) / 2;
            if (ConvexRational.From(BitConverter.UInt64BitsToDouble(middle)).CompareTo(r) <= 0) low = middle;
            else high = middle - 1;
        }
        return BitConverter.UInt64BitsToDouble(low);
    }
}

internal readonly record struct ConvexExactPoint(ConvexQuadratic X, ConvexQuadratic Y, ConvexQuadratic Z)
{
    internal static ConvexExactPoint From(Vector3 v) => new(ConvexQuadratic.From(v.X), ConvexQuadratic.From(v.Y), ConvexQuadratic.From(v.Z));
    internal GeometryVector Bounds => new(X.Bounds, Y.Bounds, Z.Bounds);
    public static ConvexExactPoint operator +(ConvexExactPoint a, ConvexExactPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static ConvexExactPoint operator -(ConvexExactPoint a, ConvexExactPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static ConvexExactPoint operator *(ConvexExactPoint a, ConvexQuadratic b) => new(a.X * b, a.Y * b, a.Z * b);
    internal static ConvexQuadratic Dot(ConvexExactPoint a, ConvexExactPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static ConvexExactPoint Cross(ConvexExactPoint a, ConvexExactPoint b) => new(a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
