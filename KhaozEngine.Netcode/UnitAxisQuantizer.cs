using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// 8-bit quantization of a unit-range axis (move/aim component) to a signed byte and back.
/// The wire codec scheme: quantization rounds away-from-zero so it is symmetric about zero.
/// Determinism note: SpaceGame dequantizes commands before they enter the host-authoritative sim,
/// so this rounding is hash-gated; it must not change.
/// </summary>
public static class UnitAxisQuantizer
{
    /// <summary>Clamp <paramref name="value"/> to [-1,1] and quantize to [-127,127].</summary>
    public static sbyte Quantize(float value)
        => (sbyte)MathF.Round(System.Math.Clamp(value, -1f, 1f) * 127f, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Dequantize a signed byte back to [-1,1] (<c>value / 127f</c>). Symmetric inverse of
    /// <see cref="Quantize"/>; the divisor is fixed by the wire codec scheme (hash-gated).
    /// The input is clamped to [-127,127] first so a hostile or garbage wire byte of -128
    /// (which <see cref="Quantize"/> never emits) cannot escape the [-1,1] contract; no value
    /// <see cref="Quantize"/> can produce is affected, so the hash-gated round-trip is unchanged.
    /// </summary>
    public static float Dequantize(sbyte value) => System.Math.Clamp((int)value, -127, 127) / 127f;
}
