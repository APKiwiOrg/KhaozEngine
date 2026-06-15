using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Content
{
    /// <summary>
    /// Parses hex colour strings (commonly from config) into an RGBA <see cref="Vector4"/> with each channel
    /// 0..1, and back. Accepts <c>#RRGGBB</c> / <c>RRGGBB</c> and <c>#RRGGBBAA</c> / <c>RRGGBBAA</c> (a missing
    /// alpha is opaque). MonoGame-free and Veldrid-free, so both the pure domain and the render stack can use it.
    /// </summary>
    public static class ColorHex
    {
        /// <summary>Parse <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (leading '#' optional) into an RGBA <see cref="Vector4"/> (0..1).</summary>
        public static Vector4 FromHex(string hex)
        {
            if (hex is null) throw new ArgumentNullException(nameof(hex));
            ReadOnlySpan<char> s = hex.AsSpan().Trim();
            if (s.Length > 0 && s[0] == '#') s = s[1..];
            if (s.Length != 6 && s.Length != 8)
                throw new FormatException($"Hex colour must be RRGGBB or RRGGBBAA, got '{hex}'.");

            byte r = ByteAt(s, 0), g = ByteAt(s, 2), b = ByteAt(s, 4);
            byte a = s.Length == 8 ? ByteAt(s, 6) : (byte)255;
            return new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        /// <summary>Format an RGBA <see cref="Vector4"/> (0..1) as <c>#RRGGBBAA</c> (clamped).</summary>
        public static string ToHex(Vector4 color)
        {
            static int C(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            return $"#{C(color.X):X2}{C(color.Y):X2}{C(color.Z):X2}{C(color.W):X2}";
        }

        static byte ByteAt(ReadOnlySpan<char> s, int start) =>
            byte.Parse(s.Slice(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
