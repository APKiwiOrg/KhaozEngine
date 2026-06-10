using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Data-driven emitter preset: all tunables for a burst of particles. Immutable;
/// derive variants with <c>with</c> expressions, e.g.
/// <c>ParticlePresets.Spark with { MaxSpeed = 120f }</c>.
/// </summary>
public sealed record ParticleEmitterConfig
{
    /// <summary>Minimum particle lifetime in seconds.</summary>
    public float MinLife { get; init; }

    /// <summary>Maximum particle lifetime in seconds.</summary>
    public float MaxLife { get; init; }

    /// <summary>Minimum initial speed (units/second).</summary>
    public float MinSpeed { get; init; }

    /// <summary>Maximum initial speed (units/second).</summary>
    public float MaxSpeed { get; init; }

    /// <summary>Particle size at spawn (pixels).</summary>
    public float StartSize { get; init; } = 1f;

    /// <summary>End size as a fraction of <see cref="StartSize"/>. 1 = constant, &lt;1 shrinks over life.</summary>
    public float EndSizeFactor { get; init; } = 1f;

    /// <summary>How the initial direction is chosen.</summary>
    public ParticleEmission Emission { get; init; } = ParticleEmission.Radial;

    /// <summary>Base direction for <see cref="ParticleEmission.Directional"/> (need not be normalized).</summary>
    public Vector2 Direction { get; init; } = new(0f, -1f);

    /// <summary>Half-angle (radians) of the directional spread cone.</summary>
    public float SpreadRadians { get; init; }

    /// <summary>Half-extent of the random spawn offset on X (pixels).</summary>
    public float JitterX { get; init; }

    /// <summary>Half-extent of the random spawn offset on Y (pixels).</summary>
    public float JitterY { get; init; }

    /// <summary>Horizontal sway frequency (radians/second). 0 disables sway.</summary>
    public float SwayFrequency { get; init; }

    /// <summary>Horizontal sway amplitude (pixels/second of positional drift).</summary>
    public float SwayAmplitude { get; init; }

    /// <summary>Constant acceleration applied to velocity each frame (e.g. gravity).</summary>
    public Vector2 Acceleration { get; init; } = Vector2.Zero;

    /// <summary>If set, particles use this color and ignore the emit base color.</summary>
    public Color? OverrideColor { get; init; }

    /// <summary>Target the emit base color is lerped toward when <see cref="OverrideColor"/> is null.</summary>
    public Color BlendTarget { get; init; } = Color.White;

    /// <summary>Lerp amount [0,1] from the emit base color toward <see cref="BlendTarget"/>.</summary>
    public float BlendAmount { get; init; }
}
