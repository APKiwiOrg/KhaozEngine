using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Retained expression operands with checked fixed-width dyadic integers. No heap fallback.
/// A failed operation poisons the current expression, even if a later cancellation could remove it.</summary>
internal sealed class SweepExactWorkspace
{
    internal const int MaximumNumbers = 2048;
    readonly Number[] _numbers = new Number[MaximumNumbers];
    int _count;
    internal bool IsResolved { get; private set; } = true;
    internal int Checkpoint => _count;

    internal void Reset() { _count = 0; IsResolved = true; }
    internal void Restore(int checkpoint) { _count = checkpoint; }
    internal void Refuse() => IsResolved = false;

    [InlineArray(128)]
    internal struct Words { uint _first; }
    internal struct Integer
    {
        internal Words Digits;
        internal int Sign, Exponent, Length;
    }
    readonly record struct Number(GeometryInterval Bounds, bool Exact, Integer Integer);

    internal int Constant(double value)
    {
        if (!double.IsFinite(value)) { Refuse(); return 0; }
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int encoded = (int)(bits >> 52) & 2047;
        ulong mantissa = bits & 0x000fffffffffffffUL;
        if (encoded != 0) mantissa |= 1UL << 52;
        Integer integer = default;
        integer.Sign = mantissa == 0 ? 0 : (bits >> 63) == 0 ? 1 : -1;
        integer.Exponent = encoded == 0 ? -1074 : encoded - 1075;
        integer.Digits[0] = (uint)mantissa;
        integer.Digits[1] = (uint)(mantissa >> 32);
        integer.Length = integer.Digits[1] != 0 ? 2 : mantissa != 0 ? 1 : 0;
        if (!BoundedGeometryArithmetic.TryNormalizeSweepInteger(ref integer)) { Refuse(); return 0; }
        return New(new(GeometryInterval.Exact(value), true, integer));
    }

    internal int Source(FeatureNumber value) => value.IsExact ? Constant(value.Value) : Enclosed(value.Bounds);
    internal int Enclosed(GeometryInterval bounds) => New(new(bounds, false, default));
    internal GeometryInterval Bounds(int value) => IsResolved ? _numbers[value].Bounds : default;
    internal bool IsExact(int value) => IsResolved && _numbers[value].Exact;
    internal GeometrySign Sign(int value)
    {
        if (!IsResolved) return GeometrySign.Unresolved;
        ref readonly Number number = ref _numbers[value];
        if (number.Exact) return number.Integer.Sign < 0 ? GeometrySign.Negative :
            number.Integer.Sign > 0 ? GeometrySign.Positive : GeometrySign.Zero;
        return number.Bounds.Lower > 0 ? GeometrySign.Positive : number.Bounds.Upper < 0
            ? GeometrySign.Negative : GeometrySign.Unresolved;
    }

    internal int Negate(int value)
    {
        if (!IsResolved) return 0;
        Number number = _numbers[value];
        Integer integer = number.Integer;
        integer.Sign = -integer.Sign;
        return New(new(GeometryInterval.Enclose(-number.Bounds.Upper, -number.Bounds.Lower), number.Exact, integer));
    }
    internal int Subtract(int a, int b) => a == b ? Constant(0) : Add(a, Negate(b));
    internal int Add(int a, int b) => Binary(a, b, false);
    internal int Multiply(int a, int b) => Binary(a, b, true);
    internal int Square(int value) => Multiply(value, value);

    int Binary(int a, int b, bool product)
    {
        if (!IsResolved) return 0;
        Number left = _numbers[a], right = _numbers[b];
        if (product && (left.Exact && left.Integer.Sign == 0 || right.Exact && right.Integer.Sign == 0))
            return Constant(0);
        GeometryInterval bounds = product ? left.Bounds.Multiply(right.Bounds) : left.Bounds.Add(right.Bounds);
        bool exact = left.Exact && right.Exact;
        Integer integer = default;
        if (exact && !(product
            ? BoundedGeometryArithmetic.TryMultiplySweepInteger(left.Integer, right.Integer, out integer)
            : BoundedGeometryArithmetic.TryAddSweepInteger(left.Integer, right.Integer, out integer)))
        { Refuse(); return 0; }
        if (exact && integer.Sign == 0) bounds = GeometryInterval.Exact(0);
        // Exact arithmetic may decide a sign after interval overflow, but never manufactures a
        // finite enclosure. Such arithmetic remains outside this numerical route.
        return New(new(bounds, exact, integer));
    }

    int New(Number value)
    {
        if (!IsResolved || !value.Bounds.IsResolved || _count == MaximumNumbers)
        { Refuse(); return 0; }
        int id = _count++;
        _numbers[id] = value;
        return id;
    }
}
