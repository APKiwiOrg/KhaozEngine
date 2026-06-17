using System;
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// An RGBA color with float channels in 0..1. A thin, typed wrapper over <see cref="Vector4"/> so call sites
    /// stop passing a bare <c>Vector4</c> for both a destination rect and a color (a swappable foot-gun). Converts
    /// implicitly to <see cref="Vector4"/> so it drops straight into the existing batcher; the reverse is explicit
    /// because not every <c>Vector4</c> is a color.
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
