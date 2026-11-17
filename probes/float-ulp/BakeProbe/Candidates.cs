using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

// Candidate shapes for a one-ULP float step and for a comparison against the smallest subnormal.
// Each is a struct so a generic method instantiated over it gets its own specialized machine code.
interface IStep
{
    static abstract float Down(float x);
    static abstract float Up(float x);
    // x >= float.Epsilon, i.e. x is positive and not zero (+Infinity included, NaN excluded).
    static abstract bool AtLeastEpsilon(float x);
}

// Today's expression: the BCL bodies inline, and their zero branch returns a subnormal float constant.
struct Bcl : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Down(float x) => MathF.BitDecrement(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Up(float x) => MathF.BitIncrement(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool AtLeastEpsilon(float x) => x >= float.Epsilon;
}

// Candidate A: integer domain, branchless. No float constant exists in the body, and the result reaches the
// float domain only through a bit move of a run-time integer. A JIT-known argument still folds the integer.
struct IntegerSteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint b = bits | ((bits == 0 ? 1u : 0u) << 31);          // +0 steps like -0
        uint next = b + ((b >> 31) << 1) - 1u;                   // negative: +1, positive: -1
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0xFF800000u); // NaN or -Infinity
        return BitConverter.UInt32BitsToSingle(keep ? bits : next);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint b = bits & ~((bits == 0x80000000u ? 1u : 0u) << 31); // -0 steps like +0
        uint next = b + 1u - ((b >> 31) << 1);                    // positive: +1, negative: -1
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0x7F800000u); // NaN or +Infinity
        return BitConverter.UInt32BitsToSingle(keep ? bits : next);
    }

    // Comparison on the bit pattern: positive, non-zero, not NaN.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => BitConverter.SingleToUInt32Bits(x) - 1u < 0x7F800000u;
}

// Storage the JIT cannot treat as a constant: plain mutable statics, never written after initialization.
static class OpaqueBits
{
#pragma warning disable IDE0044 // Must stay mutable: a readonly static is a JIT-time constant once its class is initialized.
    internal static uint s_negativeEpsilon = 0x80000001u;
    internal static uint s_positiveEpsilon = 0x00000001u;
#pragma warning restore IDE0044
}

// Candidate B: the BCL bodies with the zero branch reading its bits from opaque storage.
struct OpaqueSteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        if (!float.IsFinite(x)) return bits == 0x7F800000u ? float.MaxValue : x;
        if (bits == 0) return BitConverter.UInt32BitsToSingle(OpaqueBits.s_negativeEpsilon);
        return BitConverter.UInt32BitsToSingle(float.IsNegative(x) ? bits + 1 : bits - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        if (!float.IsFinite(x)) return bits == 0xFF800000u ? float.MinValue : x;
        if (bits == 0x80000000u) return BitConverter.UInt32BitsToSingle(OpaqueBits.s_positiveEpsilon);
        return BitConverter.UInt32BitsToSingle(float.IsNegative(x) ? bits - 1 : bits + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => x >= BitConverter.UInt32BitsToSingle(OpaqueBits.s_positiveEpsilon);
}

// Candidate C (recommended): integer domain with the zero case from opaque storage, so a JIT-known zero argument
// cannot fold the result either. It delegates to the prototype file so the proof covers that exact source.
// The comparison is restated with a normal constant: x > 0 is x >= Epsilon.
struct HybridSteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Down(float x) => KhaozEngine.Numerics.FloatUlp.Down(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Up(float x) => KhaozEngine.Numerics.FloatUlp.Up(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool AtLeastEpsilon(float x) => x > 0f;
}

static class ReadonlyBits
{
    internal static readonly uint NegativeEpsilon = 0x80000001u;
    internal static readonly uint PositiveEpsilon = 0x00000001u;
}

// Control D: a static readonly field. Expected to fold once the class is initialized before the compile.
struct ReadonlyStaticSteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x) => BitConverter.SingleToUInt32Bits(x) == 0
        ? BitConverter.UInt32BitsToSingle(ReadonlyBits.NegativeEpsilon) : IntegerSteps.Down(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x) => BitConverter.SingleToUInt32Bits(x) == 0x80000000u
        ? BitConverter.UInt32BitsToSingle(ReadonlyBits.PositiveEpsilon) : IntegerSteps.Up(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => x >= BitConverter.UInt32BitsToSingle(ReadonlyBits.PositiveEpsilon);
}

// Candidate E: a volatile read of the readonly field, which keeps the field readonly for analyzers.
struct VolatileReadonlySteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x) => BitConverter.SingleToUInt32Bits(x) == 0
        ? BitConverter.UInt32BitsToSingle(Volatile.Read(in ReadonlyBits.NegativeEpsilon)) : IntegerSteps.Down(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x) => BitConverter.SingleToUInt32Bits(x) == 0x80000000u
        ? BitConverter.UInt32BitsToSingle(Volatile.Read(in ReadonlyBits.PositiveEpsilon)) : IntegerSteps.Up(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => x >= BitConverter.UInt32BitsToSingle(Volatile.Read(in ReadonlyBits.PositiveEpsilon));
}

// Control F: bits in RVA data. The JIT can read constant RVA data at compile time.
struct RvaSteps : IStep
{
    static ReadOnlySpan<byte> NegativeEpsilon => [0x01, 0x00, 0x00, 0x80];
    static ReadOnlySpan<byte> PositiveEpsilon => [0x01, 0x00, 0x00, 0x00];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x) => BitConverter.SingleToUInt32Bits(x) == 0
        ? BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(NegativeEpsilon)) : IntegerSteps.Down(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x) => BitConverter.SingleToUInt32Bits(x) == 0x80000000u
        ? BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(PositiveEpsilon)) : IntegerSteps.Up(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => x >= BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(PositiveEpsilon));
}

// Control G: the zero branch as a reinterpretation of constant bits, the obvious rewrite.
struct BitsLiteralSteps : IStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x) => BitConverter.SingleToUInt32Bits(x) == 0
        ? BitConverter.UInt32BitsToSingle(0x80000001u) : IntegerSteps.Down(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x) => BitConverter.SingleToUInt32Bits(x) == 0x80000000u
        ? BitConverter.UInt32BitsToSingle(0x00000001u) : IntegerSteps.Up(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AtLeastEpsilon(float x) => x >= BitConverter.UInt32BitsToSingle(0x00000001u);
}
