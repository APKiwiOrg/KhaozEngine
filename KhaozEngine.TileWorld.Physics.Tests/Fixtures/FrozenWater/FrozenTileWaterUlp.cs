using System;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen for #1832. The one-ULP float steps the frozen water reference uses, a test-side copy that never changes with
// the production FloatUlp. The BCL steps hold -float.Epsilon and float.Epsilon in their zero branch, and the frozen
// reference runs in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests under flush modes, where a step first optimized
// under flush to zero would keep a signed zero for the whole process. These steps compute in the integer domain and
// read the results for a zero argument from mutable statics the JIT cannot fold. Their bits match MathF.BitDecrement
// and MathF.BitIncrement for every input.

/// <summary>One-ULP float steps for the frozen water reference, without a JIT-visible subnormal constant.</summary>
internal static class FrozenTileWaterUlp
{
    // Mutable on purpose, and never written: the JIT folds a static readonly field into a constant.
    static uint s_negativeEpsilonBits = 0x80000001u;
    static uint s_positiveEpsilonBits = 0x00000001u;

    /// <summary>The next float toward negative infinity.</summary>
    internal static float Down(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint next = bits + ((bits >> 31) << 1) - 1u;
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0xFF800000u);
        return BitConverter.UInt32BitsToSingle((bits << 1) == 0u ? s_negativeEpsilonBits : keep ? bits : next);
    }

    /// <summary>The next float toward positive infinity.</summary>
    internal static float Up(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint next = bits + 1u - ((bits >> 31) << 1);
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0x7F800000u);
        return BitConverter.UInt32BitsToSingle((bits << 1) == 0u ? s_positiveEpsilonBits : keep ? bits : next);
    }
}
