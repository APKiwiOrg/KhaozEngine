using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Primitives
{
    /// <summary>
    /// An RGBA color with float channels in 0..1. A typed wrapper over <see cref="Vector4"/> so call sites
    /// stop passing a bare <c>Vector4</c> for both a destination rect and a color (a swappable foot-gun).
    /// Converts implicitly to <see cref="Vector4"/> so it drops straight into GPU layout structs; the reverse
    /// is explicit because not every <c>Vector4</c> is a color.
    /// </summary>
    public readonly struct Color : IEquatable<Color>
    {
        public readonly float R, G, B, A;

        public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

        /// <summary>From 0..255 byte channels (alpha defaults to opaque).</summary>
        public static Color FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        public Vector4 ToVector4() => new(R, G, B, A);
        public static Color FromVector4(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

        public static implicit operator Vector4(Color c) => c.ToVector4();
        public static explicit operator Color(Vector4 v) => FromVector4(v);

        /// <summary>The same color with a replaced alpha.</summary>
        public Color WithAlpha(float a) => new(R, G, B, a);

        /// <summary>The same color with R, G, B scaled by <paramref name="factor"/> and alpha preserved -
        /// dims (or brightens) the visible color without touching opacity. Unclamped. Prefer this over
        /// <c>* float</c> for tinting under straight-alpha blending (the 2D batch's SourceAlpha /
        /// InverseSourceAlpha): <c>* float</c> scales <see cref="A"/> too, so <c>color * 0.6f</c> meant to
        /// dim by 40% instead makes the sprite 40% translucent (opaque content beneath bleeds through), and
        /// a faint <c>color * 0.13f</c> also attenuates alpha until it rounds below one gray level and the
        /// draw renders invisible.</summary>
        public Color ScaleRgb(float factor) => new(R * factor, G * factor, B * factor, A);

        /// <summary>The same as <see cref="ScaleRgb(float)"/> (R/G/B scaled by <paramref name="factor"/>, alpha
        /// preserved) but each scaled channel is clamped to 0..1. Prefer this over <see cref="ScaleRgb(float)"/>
        /// whenever <paramref name="factor"/> can push a channel out of range, e.g. a UI "selected" brighten tint
        /// on an already-bright base color: an unclamped overshoot past 1.0 reads back through downstream
        /// unclamped math (a further blend, a GPU write) as an out-of-range channel instead of simply saturating
        /// white the way a human eye expects "brighter" to cap out.</summary>
        public Color ScaleRgbClamped(float factor) => new(
            Math.Clamp(R * factor, 0f, 1f), Math.Clamp(G * factor, 0f, 1f), Math.Clamp(B * factor, 0f, 1f), A);

        /// <summary>Scale all four channels (including alpha) by <paramref name="s"/>. Unclamped; matches
        /// <see cref="Vector4"/> <c>* float</c> and legacy MonoGame <c>Color * float</c>. To dim the visible
        /// color without changing opacity under straight-alpha blending, use <see cref="ScaleRgb(float)"/>.</summary>
        public static Color operator *(Color c, float s) => new(c.R * s, c.G * s, c.B * s, c.A * s);

        /// <summary>Scalar multiply (symmetric with <see cref="op_Multiply(Color,float)"/>).</summary>
        public static Color operator *(float s, Color c) => new(c.R * s, c.G * s, c.B * s, c.A * s);

        /// <summary>Component-wise linear interpolation, unclamped and byte-identical to
        /// <see cref="Vector4.Lerp(Vector4,Vector4,float)"/> (it delegates to it): <c>a + (b - a) * t</c> per
        /// channel. <paramref name="t"/> is NOT clamped and no rounding through bytes occurs.</summary>
        public static Color Lerp(Color a, Color b, float t) => FromVector4(Vector4.Lerp(a.ToVector4(), b.ToVector4(), t));

        /// <summary>Parse <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (leading '#' optional). Missing alpha is opaque.</summary>
        public static Color FromHex(string hex)
        {
            if (hex is null) throw new ArgumentNullException(nameof(hex));
            ReadOnlySpan<char> s = hex.AsSpan().Trim();
            if (s.Length > 0 && s[0] == '#') s = s[1..];
            if (s.Length != 6 && s.Length != 8)
                throw new FormatException($"Hex colour must be RRGGBB or RRGGBBAA, got '{hex}'.");
            byte r = ByteAt(s, 0), g = ByteAt(s, 2), b = ByteAt(s, 4);
            byte a = s.Length == 8 ? ByteAt(s, 6) : (byte)255;
            return FromBytes(r, g, b, a);
        }

        /// <summary>Format as <c>#RRGGBBAA</c> (channels clamped).</summary>
        public static string ToHex(Color c)
        {
            static int Ch(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            return $"#{Ch(c.R):X2}{Ch(c.G):X2}{Ch(c.B):X2}{Ch(c.A):X2}";
        }

        static byte ByteAt(ReadOnlySpan<char> s, int start) =>
            byte.Parse(s.Slice(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static readonly Color White = new(1f, 1f, 1f, 1f);
        public static readonly Color Black = new(0f, 0f, 0f, 1f);
        public static readonly Color Transparent = new(0f, 0f, 0f, 0f);

        public bool Equals(Color o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object? o) => o is Color c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public static bool operator ==(Color a, Color b) => a.Equals(b);
        public static bool operator !=(Color a, Color b) => !a.Equals(b);
        public override string ToString() => $"Color({R}, {G}, {B}, {A})";
    }
}
