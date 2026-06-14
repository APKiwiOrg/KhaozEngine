using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Shared color parsing utilities.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Multiplies the RGB channels by <paramref name="factor"/> (alpha unchanged), clamping each to
    /// 0..255. A factor below 1 darkens (useful for shaded faces); above 1 lightens. Negative
    /// factors are treated as 0.
    /// </summary>
    /// <param name="color">The colour to scale.</param>
    /// <param name="factor">Per-channel multiplier; values &lt; 0 clamp to 0.</param>
    public static Color Scale(Color color, float factor)
    {
        if (factor < 0f) factor = 0f;
        return new Color(
            (int)Math.Clamp(color.R * factor, 0f, 255f),
            (int)Math.Clamp(color.G * factor, 0f, 255f),
            (int)Math.Clamp(color.B * factor, 0f, 255f),
            color.A);
    }

    /// <summary>
    /// Parses a hex color string (e.g., "#B87333" or "B87333") into a <see cref="Color"/>.
    /// Returns <paramref name="fallback"/> if the string is malformed.
    /// </summary>
    /// <param name="hex">The hex color string to parse.</param>
    /// <param name="fallback">Color to return on parse failure. Defaults to <see cref="Color.Gray"/>.</param>
    public static Color ParseHex(string hex, Color? fallback = null)
    {
        if (hex.StartsWith('#')) hex = hex[1..];

        if (hex.Length == 6 &&
            int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out int r) &&
            int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g) &&
            int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return new Color(r, g, b);
        }

        return fallback ?? Color.Gray;
    }
}
