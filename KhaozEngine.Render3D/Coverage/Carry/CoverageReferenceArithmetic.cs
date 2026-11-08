using System;

namespace KhaozEngine.Render3D;

/// <summary>Retained scalar binary32 operations. Flush each operand and result independently, preserving zero signs.</summary>
internal sealed class CoverageReferenceArithmetic
{
    internal float Add(float a, float b) => Flush(Flush(a) + Flush(b));
    internal float Sub(float a, float b) => Flush(Flush(a) - Flush(b));
    internal float Mul(float a, float b) => Flush(Flush(a) * Flush(b));
    internal float Div(float a, float b) => Flush(Flush(a) / Flush(b));
    internal float Sqrt(float a) => Flush(MathF.Sqrt(Flush(a)));
    internal float Abs(float a) => Flush(MathF.Abs(Flush(a)));
    internal float Neg(float a) => Flush(-Flush(a));
    internal float Min(float a, float b) => Flush(MathF.Min(Flush(a), Flush(b)));
    internal float Max(float a, float b) => Flush(MathF.Max(Flush(a), Flush(b)));
    internal float Clamp(float value, float min, float max) => Flush(Math.Clamp(Flush(value), Flush(min), Flush(max)));
    internal float Read(float value) => Flush(value);
    internal bool Less(float a, float b) => Flush(a) < Flush(b);
    internal bool LessOrEqual(float a, float b) => Flush(a) <= Flush(b);
    internal bool Greater(float a, float b) => Flush(a) > Flush(b);
    internal bool GreaterOrEqual(float a, float b) => Flush(a) >= Flush(b);
    internal bool Equal(float a, float b) => Flush(a) == Flush(b);
    internal bool NotEqual(float a, float b) => Flush(a) != Flush(b);
    internal bool IsFinite(float value) => IsFinite(ref value);

    internal bool IsFinite(ref float value)
    {
        value = Flush(value);
        return (BitConverter.SingleToUInt32Bits(value) & 0x7f800000u) != 0x7f800000u;
    }

    static float Flush(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value), magnitude = bits & 0x7fffffffu;
        // Inclusive B38 includes 2^-126, making before-rounding and after-rounding tininess modes equivalent.
        return magnitude == 0 || magnitude > 0x00800000u ? value : BitConverter.UInt32BitsToSingle(bits & 0x80000000u);
    }
}
