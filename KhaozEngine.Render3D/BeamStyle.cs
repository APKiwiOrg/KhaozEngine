using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Tunables for a 3D energy beam (see <see cref="Scene3D.DrawBeam"/>). Immutable; derive variants with
    /// <c>with</c> (the idiom is <c>BeamStyle.Default with { ... }</c>, which keeps the sensible shape defaults).
    /// A bright inner core (<see cref="CoreColor"/>, the inner <see cref="CoreFraction"/> of the width) sits inside
    /// a softer halo (<see cref="GlowColor"/>, falling off across the full width by <see cref="GlowSoftness"/>).
    /// Both colours default (null) to the <see cref="Scene3D.DrawBeam"/> colour argument: the core uses it directly,
    /// the halo a 0.4x-alpha copy. Optional end <see cref="Taper"/>, brightness <see cref="PulseSpeed"/>/
    /// <see cref="PulseAmount"/>, and along-beam <see cref="ScrollSpeed"/> flow all read
    /// <see cref="Scene3D.EffectTimeSeconds"/>. Vocabulary mirrors the 2D
    /// <see cref="KhaozEngine.Render2D.Vfx.BeamParams"/>.
    /// </summary>
    public readonly record struct BeamStyle
    {
        /// <summary>Bright inner-core colour. Null =&gt; the <see cref="Scene3D.DrawBeam"/> colour argument.</summary>
        public Color? CoreColor { get; init; }

        /// <summary>Soft halo colour. Null =&gt; the resolved core colour at 0.4x alpha (a dimmer wash of the same hue).</summary>
        public Color? GlowColor { get; init; }

        /// <summary>Bright-core share of the half-width, in [0,1]. Default 0.35.</summary>
        public float CoreFraction { get; init; }

        /// <summary>Halo falloff exponent (higher = tighter halo hugging the core). Default 2.</summary>
        public float GlowSoftness { get; init; }

        /// <summary>End-fade fraction in [0,0.5]: the beam fades in over this fraction of its length at each end.
        /// 0 (default) = square ends.</summary>
        public float Taper { get; init; }

        /// <summary>Brightness pulse speed (radians/second). 0 (default) disables pulsing.</summary>
        public float PulseSpeed { get; init; }

        /// <summary>Pulse amplitude in [0,1]: fraction by which brightness oscillates. Default 0.</summary>
        public float PulseAmount { get; init; }

        /// <summary>Along-beam flow speed (cycles/second) of the core's brightness ripple. 0 (default) = no flow.</summary>
        public float ScrollSpeed { get; init; }

        /// <summary>A sensible starting point: hue-neutral (the <see cref="Scene3D.DrawBeam"/> colour tints both
        /// bands), a 35%-of-half-width bright core in a soft halo, square ends, static.</summary>
        public static BeamStyle Default => new()
        {
            CoreColor = null,
            GlowColor = null,
            CoreFraction = 0.35f,
            GlowSoftness = 2f,
            Taper = 0f,
            PulseSpeed = 0f,
            PulseAmount = 0f,
            ScrollSpeed = 0f,
        };
    }
}
