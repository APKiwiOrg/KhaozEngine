using System;
using Microsoft.Xna.Framework;

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
        => (sbyte)MathF.Round(MathHelper.Clamp(value, -1f, 1f) * 127f, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Dequantize a signed byte back to [-1,1] (<c>value / 127f</c>). Symmetric inverse of
    /// <see cref="Quantize"/>; the divisor is fixed by the wire codec scheme (hash-gated).
    /// </summary>
    public static float Dequantize(sbyte value) => value / 127f;
}
