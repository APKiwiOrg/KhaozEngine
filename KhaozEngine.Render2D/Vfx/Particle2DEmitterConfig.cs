using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Data-driven preset for a burst of 2D particles - every tunable for <see cref="Particle2DSystem.Emit(in KhaozEngine.Render2D.Vfx.Particle2DEmitterConfig, System.Numerics.Vector2, int)"/>.
    /// Immutable; derive variants with <c>with</c> expressions, e.g.
    /// <c>cfg with { MaxSpeed = 120f }</c>. Build these from data so a consumer can keep presets in content.
    /// </summary>
    public readonly record struct Particle2DEmitterConfig
    {
        /// <summary>Field initializers run via this parameterless constructor (required for a struct with defaults).</summary>
        public Particle2DEmitterConfig() { }

        /// <summary>Minimum particle lifetime in seconds.</summary>
        public float MinLife { get; init; }

        /// <summary>Maximum particle lifetime in seconds.</summary>
        public float MaxLife { get; init; }

        /// <summary>Minimum initial speed (pixels/second).</summary>
        public float MinSpeed { get; init; }

        /// <summary>Maximum initial speed (pixels/second).</summary>
        public float MaxSpeed { get; init; }

        /// <summary>Particle size at spawn (pixels).</summary>
        public float StartSize { get; init; } = 1f;

        /// <summary>Particle size at end of life (pixels); linearly interpolated from <see cref="StartSize"/>.</summary>
        public float EndSize { get; init; } = 1f;

        /// <summary>How the initial direction is chosen.</summary>
        public Particle2DEmission Emission { get; init; } = Particle2DEmission.Radial;

        /// <summary>Base direction for <see cref="Particle2DEmission.Directional"/> (need not be normalized).</summary>
        public Vector2 Direction { get; init; } = new(0f, -1f);

        /// <summary>Half-angle (radians) of the directional spread cone.</summary>
        public float SpreadRadians { get; init; }

        /// <summary>Half-extent of the random spawn offset on X (pixels).</summary>
        public float JitterX { get; init; }

        /// <summary>Half-extent of the random spawn offset on Y (pixels).</summary>
        public float JitterY { get; init; }

        /// <summary>Constant acceleration applied to velocity each step (e.g. gravity).</summary>
        public Vector2 Acceleration { get; init; } = Vector2.Zero;

        /// <summary>Velocity damping per second: each step multiplies velocity by <c>max(0, 1 - Drag*dt)</c>. 0 = none.</summary>
        public float Drag { get; init; }

        /// <summary>Horizontal sway frequency (radians/second). 0 disables sway.</summary>
        public float SwayFrequency { get; init; }

        /// <summary>Horizontal sway amplitude (pixels/second of positional drift).</summary>
        public float SwayAmplitude { get; init; }

        /// <summary>Half-extent of the random initial rotation (radians). 0 spawns unrotated.</summary>
        public float RotationJitter { get; init; }

        /// <summary>Minimum angular velocity (radians/second).</summary>
        public float MinAngularVelocity { get; init; }

        /// <summary>Maximum angular velocity (radians/second).</summary>
        public float MaxAngularVelocity { get; init; }

        /// <summary>Colour at spawn (multiplied by the emit tint). Lerps toward <see cref="EndColor"/> over life.</summary>
        public Color StartColor { get; init; } = Color.White;

        /// <summary>Colour at end of life (multiplied by the emit tint). Set its alpha to 0 to fade out.</summary>
        public Color EndColor { get; init; } = Color.White;

        /// <summary>Compositing mode for this preset's particles (see <see cref="BlendMode"/>).</summary>
        public BlendMode Blend { get; init; } = BlendMode.Alpha;

        /// <summary>
        /// Fade-IN leg of a trapezoid alpha envelope, in seconds: a particle's alpha ramps 0 -> 1 over the first
        /// <see cref="FadeInDuration"/> seconds of life, then holds. Default 0 (no fade-in - alpha is full at spawn,
        /// today's behaviour). Combine with <see cref="FadeOutDuration"/> for a fade-in / hold / fade-out shape,
        /// which is what a persistent ambient field (dust, embers, snow) needs so particles appear and disappear
        /// softly instead of popping. The envelope multiplies the particle's current colour alpha, so the
        /// <see cref="StartColor"/>-&gt;<see cref="EndColor"/> colour lerp still applies on top.
        /// </summary>
        public float FadeInDuration { get; init; }

        /// <summary>
        /// Fade-OUT leg of the trapezoid alpha envelope, in seconds: a particle's alpha ramps 1 -> 0 over the last
        /// <see cref="FadeOutDuration"/> seconds of life. Default 0 (the envelope adds no fade-out - the existing
        /// colour-alpha lerp, e.g. an <see cref="EndColor"/> with alpha 0, still fades the particle if configured).
        /// </summary>
        public float FadeOutDuration { get; init; }

        /// <summary>
        /// Per-particle random size variation, as a fraction of the configured size. At emit each particle draws a
        /// scale in <c>[1 - SizeJitter, 1 + SizeJitter]</c> (clamped at 0) and multiplies both
        /// <see cref="StartSize"/> and <see cref="EndSize"/> by it, so a field of motes gets natural size spread
        /// while still lerping proportionally. Default 0 (every particle the exact configured size, today's
        /// behaviour); e.g. 0.4 = +/-40%.
        /// </summary>
        public float SizeJitter { get; init; }
    }
}
