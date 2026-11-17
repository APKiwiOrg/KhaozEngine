using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Numerics;

/// <summary>
/// One-ULP float steps that never put a subnormal float constant in front of the JIT.
/// </summary>
/// <remarks>
/// The .NET JIT folds float constants through the floating point controls of the thread that compiles the
/// method. An optimized compile (AggressiveOptimization, on-stack replacement, tiering off) on a thread with
/// flush to zero set stores <c>MathF.BitDecrement(0f)</c>'s <c>-float.Epsilon</c> as <c>-0f</c>, and that machine
/// code then serves every thread. These steps compute in the integer domain and take the only subnormal result
/// they need, the zero case, from storage the JIT cannot fold. Bits are identical to
/// <see cref="MathF.BitDecrement(float)"/> and <see cref="MathF.BitIncrement(float)"/> for every input.
/// </remarks>
internal static class FloatUlp
{
    // Must stay mutable: the JIT folds a static readonly field into a constant once its class is initialized,
    // and folding the bits back to a float runs through the same flushed conversion.
#pragma warning disable IDE0044
    private static uint s_negativeEpsilonBits = 0x80000001u;
    private static uint s_positiveEpsilonBits = 0x00000001u;
#pragma warning restore IDE0044

    /// <summary>The next float toward negative infinity, bit-identical to <see cref="MathF.BitDecrement(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint next = bits + ((bits >> 31) << 1) - 1u;                                   // negative: +1, positive: -1
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0xFF800000u);      // NaN or -Infinity
        uint result = keep ? bits : next;
        return BitConverter.UInt32BitsToSingle(bits == 0 ? s_negativeEpsilonBits : result);
    }

    /// <summary>The next float toward positive infinity, bit-identical to <see cref="MathF.BitIncrement(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint next = bits + 1u - ((bits >> 31) << 1);                                   // positive: +1, negative: -1
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0x7F800000u);      // NaN or +Infinity
        uint result = keep ? bits : next;
        return BitConverter.UInt32BitsToSingle(bits == 0x80000000u ? s_positiveEpsilonBits : result);
    }

    /// <summary>The smallest positive subnormal, read at run time.</summary>
    public static float Epsilon => BitConverter.UInt32BitsToSingle(s_positiveEpsilonBits);
}
