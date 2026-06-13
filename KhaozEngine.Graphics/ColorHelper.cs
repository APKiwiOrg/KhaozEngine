using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Shared color parsing utilities.
/// </summary>
public static class ColorHelper
{
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
