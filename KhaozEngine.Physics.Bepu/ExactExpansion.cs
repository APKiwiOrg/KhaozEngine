using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Allocation-free exact signs over binary64 operands, by error-free transformations and expansion
/// arithmetic (Shewchuk, Adaptive Precision Floating-Point Arithmetic, 1997). Every operand passed here is zero or
/// has a magnitude in [2^-200, 2^200), which every finite binary32 value does. Every operand is then a multiple of
/// 2^-252, and so is each Two-Sum part of a difference of two operands. The predicates here multiply at most three
/// such values, so every product and its rounding error are multiples of 2^-756, far above the smallest subnormal,
/// and no magnitude reaches 2^610. Two-Sum and the fused Two-Product are then exact, Grow-Expansion keeps a
/// nonoverlapping expansion of the exact sum, and its largest component carries the sign.
/// <see cref="BoundedGeometryArithmetic"/> takes this path only inside that domain, where its dyadic path never
/// reaches the 4096 bit cap either, so both paths return the same exact sign.</summary>
internal ref struct ExactExpansion
{
    // Operand exponents accepted by InRange: |x| in [2^-200, 2^200).
    const int LowestExponent = 1023 - 200, HighestExponent = 1023 + 199;

    readonly Span<double> _components;
    int _count;

    /// <summary>An empty expansion over <paramref name="buffer"/>, which must hold one more component than the
    /// number of terms added.</summary>
    internal ExactExpansion(Span<double> buffer)
    {
        _components = buffer;
        _count = 0;
    }

    /// <summary>Zero, or a finite magnitude in [2^-200, 2^200).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool InRange(double value)
    {
        int exponent = (int)((ulong)BitConverter.DoubleToInt64Bits(value) >> 52) & 0x7ff;
        return value == 0 || (exponent >= LowestExponent && exponent <= HighestExponent);
    }

    /// <summary>Knuth's Two-Sum: <c>a + b == sum + error</c> exactly for finite operands whose sum does not
    /// overflow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TwoSum(double a, double b, out double sum, out double error)
    {
        sum = a + b;
        double bVirtual = sum - a;
        double aVirtual = sum - bVirtual;
        error = (a - aVirtual) + (b - bVirtual);
    }

    /// <summary>Adds one term: Grow-Expansion with zero elimination (Shewchuk, Theorem 10). The components stay
    /// nonoverlapping and increasing in magnitude.</summary>
    internal void Add(double term)
    {
        if (term == 0) return;
        double q = term;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            TwoSum(q, _components[i], out q, out double error);
            if (error != 0) _components[kept++] = error;
        }
        if (q != 0 || kept == 0) _components[kept++] = q;
        _count = kept;
    }

    /// <summary>Adds <c>a * b</c> exactly as its rounded product and the product's rounding error.</summary>
    internal void AddProduct(double a, double b)
    {
        double product = a * b;
        Add(Math.FusedMultiplyAdd(a, b, -product));
        Add(product);
    }

    /// <summary>The sign of the exact sum. The largest component of a nonoverlapping expansion exceeds the
    /// magnitude of all the others together.</summary>
    internal readonly GeometrySign Sign
    {
        get
        {
            double top = _count == 0 ? 0 : _components[_count - 1];
            return top > 0 ? GeometrySign.Positive : top < 0 ? GeometrySign.Negative : GeometrySign.Zero;
        }
    }
}
