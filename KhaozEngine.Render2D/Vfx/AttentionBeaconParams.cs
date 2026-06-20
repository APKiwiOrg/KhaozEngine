using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>How an attention-beacon glint is drawn around the center.</summary>
    public enum GlintStyle
    {
        /// <summary>A small soft glow dot (the radial glow texture).</summary>
        Disc = 0,

        /// <summary>A tiny 4-point sparkle: two crossed soft quads stretched from the glow texture. The default.</summary>
        Star = 1,
    }

    /// <summary>
    /// Tunables for an additive "attention" pulse (see <see cref="AttentionBeacon.Draw"/>): expanding sonar-ping
    /// rings plus a ring of twinkling glints around a point. Immutable; derive variants with <c>with</c>. A bare
    /// <c>new()</c> is all-zero (no rings, no glints - a no-op); use <see cref="Default"/> for the sensible preset.
    /// </summary>
    public readonly record struct AttentionBeaconParams
    {
        /// <summary>Tint for the rings and glints.</summary>
        public Color Color { get; init; }

        /// <summary>Master alpha multiplier in [0,1] applied to every ring and glint.</summary>
        public float Intensity { get; init; }

        /// <summary>Number of expanding sonar rings. 0 disables the rings.</summary>
        public int RingCount { get; init; }

        /// <summary>Seconds for a ring to expand from <see cref="InnerRadius"/> to <see cref="MaxRadius"/> and reset.</summary>
        public float RingPeriod { get; init; }

        /// <summary>Radius (pixels) a ring starts at.</summary>
        public float InnerRadius { get; init; }

        /// <summary>Radius (pixels) a ring fades out at.</summary>
        public float MaxRadius { get; init; }

        /// <summary>Relative ring band thickness: 1 = the ring texture's native band, &lt;1 tighter, &gt;1 thicker.</summary>
        public float RingThickness { get; init; }

        /// <summary>Number of twinkling glints around the center. 0 disables the glints.</summary>
        public int GlintCount { get; init; }

        /// <summary>Spread (pixels) of the glints from the center.</summary>
        public float GlintRadius { get; init; }

        /// <summary>Size (pixels) of each glint.</summary>
        public float GlintSize { get; init; }

        /// <summary>Twinkle speed (radians/second) of the glints' alpha oscillation.</summary>
        public float TwinkleRate { get; init; }

        /// <summary>Glint shape: <see cref="GlintStyle.Disc"/> or <see cref="GlintStyle.Star"/> (default).</summary>
        public GlintStyle GlintStyle { get; init; }

        /// <summary>A white pulse with 3 sonar rings and 4 twinkling star glints (a sensible starting point).</summary>
        public static AttentionBeaconParams Default => new()
        {
            Color = Color.White,
            Intensity = 1f,
            RingCount = 3,
            RingPeriod = 2.4f,
            InnerRadius = 6f,
            MaxRadius = 48f,
            RingThickness = 1f,
            GlintCount = 4,
            GlintRadius = 28f,
            GlintSize = 6f,
            TwinkleRate = 6f,
            GlintStyle = GlintStyle.Star,
        };
    }
}
