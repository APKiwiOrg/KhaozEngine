using System;

namespace KhaozEngine.Locomotion;

/// <summary>The fixed player publication grids. NetWorld delegates its existing wire codecs here.</summary>
public static class MovementStateQuantization
{
    public const float ClimbRateQuantum = MoveState.ClimbRateQuantum;
    public const float SpeedScaleQuantum = 1f / 1024f;
    public const float MaxSpeedScale = 8f;
    public const float HorizontalVelocityQuantum = 1f / 256f;
    public const float MaxHorizontalSpeed = 127f;
    public const float FacingYawQuantum = MathF.PI / 32768f;

    public static sbyte QuantizeClimbRate(float rate)
    {
        float units = Math.Clamp(float.IsNaN(rate) ? 0f : rate / ClimbRateQuantum, -127f, 127f);
        return (sbyte)RoundToEven(units);
    }
    public static float DecodeClimbRate(sbyte q) => q * ClimbRateQuantum;

    public static short QuantizeSpeedScale(float scale)
    {
        float clamped = Math.Clamp(float.IsNaN(scale) ? 1f : scale, 0f, MaxSpeedScale);
        return (short)RoundToEven((clamped - 1f) / SpeedScaleQuantum);
    }
    public static float DecodeSpeedScale(short q) => Math.Max(0f, 1f + q * SpeedScaleQuantum);

    public static short QuantizeHorizontalVelocity(float velocity)
    {
        float clamped = Math.Clamp(float.IsNaN(velocity) ? 0f : velocity, -MaxHorizontalSpeed, MaxHorizontalSpeed);
        return (short)Math.Clamp((int)RoundToEven(clamped / HorizontalVelocityQuantum), -32767, 32767);
    }
    public static float DecodeHorizontalVelocity(short q) => q * HorizontalVelocityQuantum;

    public static short QuantizeFacingYaw(float yaw) =>
        unchecked((short)(int)RoundToEven(CharacterMovement.WrapYaw(yaw) / FacingYawQuantum));
    public static float DecodeFacingYaw(short q) => q * FacingYawQuantum;

    // IEEE binary32 roundTiesToEven, including signed zero. No platform math library or ambient rounding mode.
    internal static float RoundToEven(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value), magnitude = bits & 0x7fffffff;
        uint sign = bits & 0x80000000;
        int exponent = (int)(magnitude >> 23);
        if (exponent >= 150) return value;
        if (exponent < 127)
            return BitConverter.UInt32BitsToSingle(sign | (magnitude > 0x3f000000 ? 0x3f800000u : 0u));
        uint unit = 1u << (150 - exponent), fraction = magnitude & (unit - 1);
        uint integral = magnitude & ~(unit - 1);
        if (fraction > unit / 2 || fraction == unit / 2 && (integral & unit) != 0) integral += unit;
        return BitConverter.UInt32BitsToSingle(sign | integral);
    }
}
