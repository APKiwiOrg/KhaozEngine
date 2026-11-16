using System;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen from 1599ccd75 for #1756. Do not synchronize with production optimisations.

// Directed double enclosures. Error-free sums and FMA residuals retain exact operations,
// so exactly represented faces and tangencies need no artificial uncertainty band.
internal readonly record struct FrozenTileWaterArithmetic(double Low, double High)
{
    public static implicit operator FrozenTileWaterArithmetic(KhaozEngine.TileWorld.Physics.TileWaterArithmetic value) => new(value.Low, value.High);
    public static implicit operator KhaozEngine.TileWorld.Physics.TileWaterArithmetic(FrozenTileWaterArithmetic value) => new(value.Low, value.High);

    internal static FrozenTileWaterArithmetic Point(double value) => new(value, value);
    public static FrozenTileWaterArithmetic operator +(FrozenTileWaterArithmetic a, FrozenTileWaterArithmetic b) =>
        new(Sum(a.Low, b.Low).Low, Sum(a.High, b.High).High);
    public static FrozenTileWaterArithmetic operator -(FrozenTileWaterArithmetic a, FrozenTileWaterArithmetic b) =>
        a + new FrozenTileWaterArithmetic(-b.High, -b.Low);
    public static FrozenTileWaterArithmetic operator *(FrozenTileWaterArithmetic a, FrozenTileWaterArithmetic b)
    {
        var p = Product(a.Low, b.Low);
        var q = Product(a.Low, b.High);
        var r = Product(a.High, b.Low);
        var s = Product(a.High, b.High);
        return new(Math.Min(Math.Min(p.Low, q.Low), Math.Min(r.Low, s.Low)),
            Math.Max(Math.Max(p.High, q.High), Math.Max(r.High, s.High)));
    }
    public static FrozenTileWaterArithmetic operator /(FrozenTileWaterArithmetic a, FrozenTileWaterArithmetic b)
    {
        if (b.Low <= 0 && b.High >= 0) return new(double.NegativeInfinity, double.PositiveInfinity);
        var p = Quotient(a.Low, b.Low);
        var q = Quotient(a.Low, b.High);
        var r = Quotient(a.High, b.Low);
        var s = Quotient(a.High, b.High);
        return new(Math.Min(Math.Min(p.Low, q.Low), Math.Min(r.Low, s.Low)),
            Math.Max(Math.Max(p.High, q.High), Math.Max(r.High, s.High)));
    }

    internal FrozenTileWaterArithmetic Square()
    {
        var a = Product(Low, Low);
        var b = Product(High, High);
        return new(Low <= 0 && High >= 0 ? 0 : Math.Min(a.Low, b.Low), Math.Max(a.High, b.High));
    }

    /// <summary>The exact sign of a + b + c + d for binary32 operands, through a nonoverlapping binary64
    /// expansion. Its largest nonzero component carries the sign of the whole sum.</summary>
    internal static int Sign(float a, float b, float c, float d = 0)
    {
        Span<double> expansion = stackalloc double[4];
        int count = 0;
        Grow(expansion, ref count, a);
        Grow(expansion, ref count, b);
        Grow(expansion, ref count, c);
        Grow(expansion, ref count, d);
        for (int i = count - 1; i >= 0; i--)
            if (expansion[i] != 0) return Math.Sign(expansion[i]);
        return 0;
    }

    static void Grow(Span<double> expansion, ref int count, double value)
    {
        double carry = value;
        for (int i = 0; i < count; i++)
        {
            double sum = carry + expansion[i];
            double virtualPart = sum - carry;
            expansion[i] = (carry - (sum - virtualPart)) + (expansion[i] - virtualPart);
            carry = sum;
        }
        expansion[count++] = carry;
    }

    internal FrozenTileWaterArithmetic Sqrt() => new(Root(Math.Max(0, Low)).Low, Root(Math.Max(0, High)).High);

    static FrozenTileWaterArithmetic Sum(double a, double b)
    {
        double sum = a + b, virtualB = sum - a;
        double residual = (a - (sum - virtualB)) + (b - virtualB);
        return Enclose(sum, residual);
    }

    static FrozenTileWaterArithmetic Product(double a, double b)
    {
        double product = a * b;
        return Enclose(product, Math.FusedMultiplyAdd(a, b, -product));
    }

    static FrozenTileWaterArithmetic Quotient(double a, double b)
    {
        double quotient = a / b;
        double residual = Math.FusedMultiplyAdd(-quotient, b, a);
        return Enclose(quotient, b < 0 ? -residual : residual);
    }

    static FrozenTileWaterArithmetic Root(double value)
    {
        double root = Math.Sqrt(value);
        return Enclose(root, Math.FusedMultiplyAdd(-root, root, value));
    }

    static FrozenTileWaterArithmetic Enclose(double value, double residual) => residual switch
    {
        > 0 => new(value, Math.BitIncrement(value)),
        < 0 => new(Math.BitDecrement(value), value),
        0 => Point(value),
        _ => new(double.NegativeInfinity, double.PositiveInfinity)
    };
}
