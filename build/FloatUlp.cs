using System;
using System.Runtime.CompilerServices;

// The template for the FloatUlp copy that build/FloatUlp.targets compiles into every project that sets KhaozFloatUlp
// to true. The target rewrites the namespace below to <AssemblyName>.FloatSteps and adds a global alias named
// FloatUlp, so the copy in each assembly has its own full name. One shared name would collide across
// InternalsVisibleTo (CS0436), and the engine projects grant each other internals. Issue #1832 and the KEFP001
// section of the KhaozEngine.CodeHealth.Analyzers README explain the defect this closes.
namespace KhaozEngine.FloatSteps;

/// <summary>
/// One-ULP float steps that never put a subnormal float constant in front of the JIT.
/// </summary>
/// <remarks>
/// The JIT folds float constants through the floating point controls of the thread that compiles the method. An
/// optimized compile on a thread with flush to zero set stores the <c>-float.Epsilon</c> of <c>MathF.BitDecrement(0f)</c>
/// as <c>-0f</c>, and that machine code then serves every thread. These steps compute in the integer domain without
/// branches and take the subnormal results of a zero argument from storage the JIT cannot fold. The bits
/// match <see cref="MathF.BitDecrement(float)"/> and <see cref="MathF.BitIncrement(float)"/> for every input, NaN
/// payloads and signed zeros included.
/// </remarks>
internal static class FloatUlp
{
    // Mutable on purpose, and never written. The JIT folds a static readonly field into a constant once its class is
    // initialized, and folding these bits back to a float runs through the same flushed conversion. KEFP001 reports
    // either field if it becomes readonly.
    private static uint s_negativeEpsilonBits = 0x80000001u;
    private static uint s_positiveEpsilonBits = 0x00000001u;

    /// <summary>The next float toward negative infinity, bit-identical to <see cref="MathF.BitDecrement(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Down(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        // A negative value moves away from zero (+1), a positive one toward it (-1).
        uint next = bits + ((bits >> 31) << 1) - 1u;
        // NaN keeps its payload and negative infinity stays.
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0xFF800000u);
        uint result = keep ? bits : next;
        // Both zeros step to -float.Epsilon. Taking -0 through the static too, not only +0, keeps a zero argument
        // the JIT already knows from folding the integer result into a subnormal constant.
        return BitConverter.UInt32BitsToSingle((bits << 1) == 0u ? s_negativeEpsilonBits : result);
    }

    /// <summary>The next float toward positive infinity, bit-identical to <see cref="MathF.BitIncrement(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Up(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        // A positive value moves away from zero (+1), a negative one toward it (-1).
        uint next = bits + 1u - ((bits >> 31) << 1);
        // NaN keeps its payload and positive infinity stays.
        bool keep = ((bits & 0x7FFFFFFFu) > 0x7F800000u) | (bits == 0x7F800000u);
        uint result = keep ? bits : next;
        // Both zeros step to float.Epsilon, read from the static for the same reason.
        return BitConverter.UInt32BitsToSingle((bits << 1) == 0u ? s_positiveEpsilonBits : result);
    }
}
