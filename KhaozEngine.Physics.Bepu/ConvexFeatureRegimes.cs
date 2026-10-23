using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact rational coefficient. Every unreduced intermediate obeys the existing integer ceiling.</summary>
internal readonly struct ConvexRational : IComparable<ConvexRational>
{
    internal BigInteger Numerator { get; }
    readonly BigInteger _denominator;
    internal BigInteger Denominator => _denominator.IsZero ? BigInteger.One : _denominator;
    internal int Sign => Numerator.Sign;
    internal bool IsZero => Numerator.IsZero;
    internal static ConvexRational One => new(1, 1);

    internal ConvexRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new ConvexExactRefusalException();
        Check(numerator); Check(denominator);
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        _denominator = denominator / divisor;
    }

    internal static ConvexRational From(double value)
    {
        if (!double.IsFinite(value)) throw new ConvexExactRefusalException();
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int encoded = (int)((bits >> 52) & 2047);
        BigInteger integer = bits & 0x000fffffffffffffUL;
        if (encoded != 0) integer |= BigInteger.One << 52;
        if ((bits >> 63) != 0) integer = -integer;
        int shift = encoded == 0 ? -1074 : encoded - 1075;
        return shift >= 0 ? new(integer << shift, 1) : new(integer, BigInteger.One << -shift);
    }

    static void Check(BigInteger value)
    {
        if (BigInteger.Abs(value).GetBitLength() > ConvexQueryPolicy.MaximumExactBits)
            throw new ConvexExactRefusalException();
    }

    static BigInteger Product(BigInteger a, BigInteger b)
    {
        if (a.IsZero || b.IsZero) return BigInteger.Zero;
        if (BigInteger.Abs(a).GetBitLength() + BigInteger.Abs(b).GetBitLength() > ConvexQueryPolicy.MaximumExactBits)
            throw new ConvexExactRefusalException();
        return a * b;
    }

    public static ConvexRational operator -(ConvexRational a) => new(-a.Numerator, a.Denominator);
    public static ConvexRational operator +(ConvexRational a, ConvexRational b)
    {
        if (a.IsZero) return b;
        if (b.IsZero) return a;
        BigInteger divisor = BigInteger.GreatestCommonDivisor(a.Denominator, b.Denominator);
        BigInteger left = Product(a.Numerator, b.Denominator / divisor);
        BigInteger right = Product(b.Numerator, a.Denominator / divisor);
        if (left.Sign == right.Sign && Math.Max(BigInteger.Abs(left).GetBitLength(), BigInteger.Abs(right).GetBitLength())
            >= ConvexQueryPolicy.MaximumExactBits) throw new ConvexExactRefusalException();
        return new(left + right, Product(a.Denominator, b.Denominator / divisor));
    }
    public static ConvexRational operator -(ConvexRational a, ConvexRational b) => a + -b;
    public static ConvexRational operator *(ConvexRational a, ConvexRational b)
    {
        BigInteger first = BigInteger.GreatestCommonDivisor(BigInteger.Abs(a.Numerator), b.Denominator);
        BigInteger second = BigInteger.GreatestCommonDivisor(BigInteger.Abs(b.Numerator), a.Denominator);
        return new(Product(a.Numerator / first, b.Numerator / second),
            Product(a.Denominator / second, b.Denominator / first));
    }
    public static ConvexRational operator /(ConvexRational a, ConvexRational b) =>
        a * new ConvexRational(b.Denominator, b.Numerator);
    public int CompareTo(ConvexRational other) => (this - other).Sign;
}

internal sealed class ConvexExactRefusalException : Exception;

/// <summary>Ascending exact coefficients, with no production support-map approximation.</summary>
internal sealed class ConvexPolynomial
{
    readonly ConvexRational[] _coefficients;
    readonly int _offset;
    int _length;
    internal ConvexPolynomialWorkspace? Workspace { get; }
    internal int Degree => _length - 1;
    internal bool IsZero => Degree == 0 && this[0].IsZero;
    internal ConvexRational this[int index] => index < _length ? _coefficients[_offset + index] : default;

    internal ConvexPolynomial(ConvexRational[] coefficients)
    {
        if (coefficients.Length == 0 || coefficients.Length > SweepExactWorkspace.MaximumNumbers)
            throw new ConvexExactRefusalException();
        _coefficients = (ConvexRational[])coefficients.Clone();
        _length = coefficients.Length;
        Trim();
    }

    internal ConvexPolynomial(ConvexPolynomialWorkspace workspace, ConvexRational[] coefficients, int offset, int length)
    { Workspace = workspace; _coefficients = coefficients; _offset = offset; _length = length; }

    internal void Set(int index, ConvexRational value) => _coefficients[_offset + index] = value;

    ConvexPolynomial Trim()
    {
        while (_length > 1 && this[_length - 1].IsZero) _length--;
        return this;
    }

    ConvexPolynomial Create(int length) => Workspace?.Create(length) ?? throw new ConvexExactRefusalException();

    internal static ConvexPolynomial From(params double[] coefficients) =>
        new(Array.ConvertAll(coefficients, ConvexRational.From));

    internal ConvexRational Evaluate(ConvexRational at)
    {
        ConvexRational result = this[Degree];
        for (int i = Degree - 1; i >= 0; i--) result = result * at + this[i];
        return result;
    }

    internal ConvexPolynomial Derivative()
    {
        ConvexPolynomial result = Create(Math.Max(1, Degree));
        for (int i = 1; i <= Degree; i++) result.Set(i - 1, this[i] * new ConvexRational(i, 1));
        return result.Trim();
    }

    internal ConvexPolynomial Scale(ConvexRational scale)
    {
        ConvexPolynomial result = Create(_length);
        for (int i = 0; i < _length; i++) result.Set(i, this[i] * scale);
        return result.Trim();
    }

    internal ConvexPolynomial Subtract(ConvexPolynomial other)
    {
        ConvexPolynomial result = Create(Math.Max(Degree, other.Degree) + 1);
        for (int i = 0; i <= result.Degree; i++) result.Set(i, this[i] - other[i]);
        return result.Trim();
    }

    internal ConvexPolynomial Multiply(ConvexPolynomial other)
    {
        if (Degree + other.Degree + 1 > SweepExactWorkspace.MaximumNumbers) throw new ConvexExactRefusalException();
        ConvexPolynomial result = Create(Degree + other.Degree + 1);
        for (int i = 0; i <= Degree; i++)
            for (int j = 0; j <= other.Degree; j++) result.Set(i + j, result[i + j] + this[i] * other[j]);
        return result.Trim();
    }

    internal ConvexPolynomial Divide(ConvexPolynomial divisor, ref ConvexQueryWork work, out ConvexPolynomial remainder)
    {
        if (divisor.IsZero) throw new ConvexExactRefusalException();
        ConvexPolynomial rest = Create(_length);
        for (int i = 0; i < _length; i++) rest.Set(i, this[i]);
        ConvexPolynomial quotient = Create(Math.Max(1, Degree - divisor.Degree + 1));
        for (int degree = Degree; degree >= divisor.Degree; degree--)
        {
            if (!work.Feature()) throw new ConvexExactRefusalException();
            ConvexRational scale = rest[degree] / divisor[divisor.Degree];
            quotient.Set(degree - divisor.Degree, scale);
            for (int i = 0; i <= divisor.Degree; i++)
            {
                int index = degree - divisor.Degree + i;
                rest.Set(index, rest[index] - scale * divisor[i]);
            }
        }
        remainder = rest.Trim();
        return quotient.Trim();
    }
}

/// <summary>The original inequality K + L sqrt(D) >= 0, before any squaring.</summary>
internal readonly record struct ConvexRadicalPredicate(ConvexPolynomial K, ConvexPolynomial L, ConvexPolynomial D)
{
    internal ConvexPolynomial SquaredDifference(ConvexPolynomialWorkspace workspace)
    {
        ConvexPolynomial k = workspace.Import(K), l = workspace.Import(L);
        return k.Multiply(k).Subtract(l.Multiply(l).Multiply(D));
    }
}

/// <summary>A root equation with closed feature domains and original radical signs.</summary>
internal readonly record struct ConvexFeatureRegime(ConvexPolynomial Polynomial,
    ConvexPolynomial[]? Domains = null, ConvexRadicalPredicate[]? RadicalDomains = null);

internal readonly record struct ConvexRootBracket(bool IsComplete, bool HasRoot, double Lower, double Upper);
